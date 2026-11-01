using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Settings;
using Godot;
using STS2RitsuLib.Ui.Toast;
using AiTeammate.AiTeammateCode.Voice;

namespace AiTeammate.AiTeammateCode.Live;

/// <summary>
/// Owns the live-room connection and the scrolling overlay. Connects anonymously by default;
/// when the user pastes SESSDATA + bili_jct, the AI can also answer viewers by sending danmaku.
/// In multiplayer only the host shows the stream (configurable).
/// 直播弹幕服务：连接、显示、以及登录后的 AI 弹幕回复。
/// </summary>
public sealed partial class LiveDanmakuService : Node
{
    private const double ReconnectDelaySeconds = 5;
    private const double SettingsPollSeconds = 0.5;

    // 断线重连会反复“进入直播间”，这段时间内只弹一次连接提示。
    private const double ConnectedToastCooldownSeconds = 30;

    // getDanmuInfo 是风控最敏感的接口：socket 重连时优先复用上一次握手结果，避免每次断开都
    // 重新打一轮 room_init + nav + getDanmuInfo。
    private const double HandshakeCacheSeconds = 300;

    public static LiveDanmakuService? Instance { get; private set; }

    public string StatusText { get; private set; } = "";
    public string LoginText { get; private set; } = "";

    private DanmakuOverlay? _overlay;
    private BilibiliLiveClient? _client;

    private string _signature = "";
    private int _generation;
    private volatile bool _connectInFlight;
    private double _time;
    private double _settingsTimer = SettingsPollSeconds;
    private double _reconnectAt = -1;
    private double _retryDelay = ReconnectDelaySeconds;
    private double _lastConnectedToastAt = double.NegativeInfinity;
    private bool _visible;

    private long _realRoomId;
    private long _ownUid;
    private bool _loggedIn;
    private int _hostIndex;

    private string _handshakeRoom = "";
    private BilibiliApi.DanmuServerInfo? _handshakeInfo;
    private BilibiliApi.LoginInfo? _handshakeLogin;
    private DateTime _handshakeAt = DateTime.MinValue;
    private int _disconnectStreak;

    private readonly List<DanmakuMessage> _recentForAi = new();
    private double _aiCooldown;
    private volatile bool _aiBusy;

    /// <summary>Creates the service under the game root once. 创建直播弹幕服务。</summary>
    public static LiveDanmakuService EnsureAttached(Node gameRoot)
    {
        if (Instance != null && GodotObject.IsInstanceValid(Instance) && Instance.IsInsideTree())
            return Instance;

        var service = new LiveDanmakuService { Name = "AiTeammateLiveDanmaku" };
        gameRoot.AddChild(service);
        return service;
    }

    public static string CurrentStatus =>
        Instance != null && GodotObject.IsInstanceValid(Instance)
            ? Instance.StatusText
            : AiLoc.T("live.status.idle", "未连接");

    public static string CurrentLogin =>
        Instance != null && GodotObject.IsInstanceValid(Instance)
            ? Instance.LoginText
            : AiLoc.T("live.login.tip", "未检测，点击“验证登录”");

    /// <summary>True on the host (or in singleplayer), where the overlay is shown by default.</summary>
    public static bool IsLocalHost()
    {
        try
        {
            var net = RunManager.Instance?.NetService;
            if (net == null)
                return true;
            return net.Type is NetGameType.Singleplayer or NetGameType.Host or NetGameType.None;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Forces a reconnect (settings button). 立即重连。</summary>
    public static void RequestReconnect()
    {
        var instance = Instance;
        if (instance == null || !GodotObject.IsInstanceValid(instance))
            return;

        instance._signature = "";
        instance._reconnectAt = -1;
        instance.RefreshState();
    }

    /// <summary>Checks the pasted cookies against Bilibili's nav endpoint. 验证 B 站登录。</summary>
    public static async Task<(bool Success, string Message)> VerifyLoginAsync(CancellationToken ct)
    {
        if (!BilibiliApi.HasLoginCookies)
            return (false, AiLoc.T("live.login.missing", "请先填写 SESSDATA 和 bili_jct"));

        var info = await BilibiliApi.GetLoginInfoAsync(ct);
        if (!info.IsLogin)
            return (false, AiLoc.T("live.login.failed", "登录校验失败：{Message}",
                ("Message", string.IsNullOrWhiteSpace(info.Error) ? "cookies 无效" : info.Error)));

        return (true, AiLoc.T("live.login.ok", "已登录：{Name}", ("Name", info.UserName)));
    }

    public override void _Ready()
    {
        Instance = this;
        SetProcess(true);

        _overlay = new DanmakuOverlay { Name = "DanmakuOverlay", Visible = false };
        AddChild(_overlay);

        StatusText = AiLoc.T("live.status.idle", "未连接");
        LoginText = AiLoc.T("live.login.tip", "未检测，点击“验证登录”");
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;

        StopClient(AiLoc.T("live.status.idle", "未连接"));
        _overlay = null;
    }

    public override void _Process(double delta)
    {
        _time += delta;

        _settingsTimer += delta;
        if (_settingsTimer >= SettingsPollSeconds)
        {
            _settingsTimer = 0;
            RefreshState();
        }

        try
        {
            _client?.Poll(delta);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Bilibili] 弹幕轮询异常: {ex.Message}");
            StopClient(AiLoc.T("live.status.failed", "连接失败：{Message}", ("Message", ex.Message)));
            _signature = "";
        }

        if (_reconnectAt >= 0 && _time >= _reconnectAt)
        {
            _reconnectAt = -1;
            if (_visible)
                BeginConnect();
        }

        PumpAiChat(delta);
    }

    private void RefreshState()
    {
        var settings = AiSettingsStore.Current;
        var hostAllowed = !settings.DanmakuHostOnly || IsLocalHost();
        var hasRoom = !string.IsNullOrWhiteSpace(BilibiliApi.NormalizeRoomInput(settings.BilibiliRoomId));
        _visible = settings.EnableBilibiliDanmaku && hostAllowed && hasRoom;

        if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            _overlay.Visible = _visible;

        if (!settings.EnableBilibiliDanmaku)
        {
            StopClient(AiLoc.T("live.status.disabled", "未启用"));
            _signature = "";
            return;
        }

        if (!hostAllowed)
        {
            StopClient(AiLoc.T("live.status.client_skips", "多人客户端：弹幕只显示在房主屏幕上"));
            _signature = "";
            return;
        }

        if (!hasRoom)
        {
            StopClient(AiLoc.T("live.status.no_room", "未填写直播间"));
            _signature = "";
            return;
        }

        var signature = string.Join('|',
            settings.BilibiliRoomId.Trim(),
            settings.BilibiliSessData,
            settings.BilibiliBiliJct,
            hostAllowed);
        if (signature == _signature)
            return;

        _signature = signature;
        StopClient(AiLoc.T("live.status.connecting", "正在连接…"));
        _overlay?.Clear();
        BeginConnect();
    }

    private void BeginConnect()
    {
        if (_connectInFlight)
            return;

        _connectInFlight = true;
        var generation = ++_generation;
        _ = ConnectAsync(generation);
    }

    private async Task ConnectAsync(int generation)
    {
        var settings = AiSettingsStore.Current;
        try
        {
            var roomInput = settings.BilibiliRoomId.Trim();

            if (TryReuseHandshake(roomInput, out var cachedInfo, out var cachedLogin))
            {
                MainThread.Post(() => StartSocket(generation, cachedInfo, cachedLogin));
                return;
            }

            var (realRoomId, error) = await BilibiliApi.ResolveRoomAsync(
                roomInput, CancellationToken.None);
            if (generation != _generation)
                return;

            if (error.Length > 0 || realRoomId <= 0)
            {
                ScheduleRetry(generation, AiLoc.T("live.status.failed", "连接失败：{Message}",
                    ("Message", string.IsNullOrWhiteSpace(error) ? "无法解析直播间" : error)));
                return;
            }

            var (info, infoError) = await BilibiliApi.GetDanmuInfoAsync(realRoomId, CancellationToken.None);
            if (generation != _generation)
                return;

            if (info == null)
            {
                ScheduleRetry(generation, AiLoc.T("live.status.failed", "连接失败：{Message}",
                    ("Message", string.IsNullOrWhiteSpace(infoError) ? "无法获取弹幕服务器" : infoError)));
                return;
            }

            var login = await BilibiliApi.GetLoginInfoAsync(CancellationToken.None);
            if (generation != _generation)
                return;

            CacheHandshake(roomInput, info, login);
            MainThread.Post(() => StartSocket(generation, info, login));
        }
        catch (Exception ex)
        {
            ScheduleRetry(generation, AiLoc.T("live.status.failed", "连接失败：{Message}",
                ("Message", ex.Message)));
        }
    }

    /// <summary>Reuses a recent handshake so websocket reconnects don't re-hit the risk-controlled APIs.</summary>
    private bool TryReuseHandshake(
        string roomInput,
        out BilibiliApi.DanmuServerInfo info,
        out BilibiliApi.LoginInfo login)
    {
        info = null!;
        login = null!;

        var cachedInfo = _handshakeInfo;
        var cachedLogin = _handshakeLogin;
        if (cachedInfo == null || cachedLogin == null)
            return false;

        if (!string.Equals(_handshakeRoom, roomInput, StringComparison.Ordinal))
            return false;

        if (DateTime.UtcNow - _handshakeAt > TimeSpan.FromSeconds(HandshakeCacheSeconds))
            return false;

        info = cachedInfo;
        login = cachedLogin;
        return true;
    }

    private void CacheHandshake(string roomInput, BilibiliApi.DanmuServerInfo info, BilibiliApi.LoginInfo login)
    {
        _handshakeRoom = roomInput;
        _handshakeInfo = info;
        _handshakeLogin = login;
        _handshakeAt = DateTime.UtcNow;
        // 新握手成功，重连失败计数从头开始。
        _disconnectStreak = 0;
    }

    private void InvalidateHandshake()
    {
        _handshakeRoom = "";
        _handshakeInfo = null;
        _handshakeLogin = null;
        _handshakeAt = DateTime.MinValue;
    }

    private void ScheduleRetry(int generation, string status)
    {
        void Apply()
        {
            if (generation != _generation)
                return;

            _connectInFlight = false;
            StatusText = status;
            _reconnectAt = _time + _retryDelay;
            // Risk-control errors should not be hammered every few seconds.
            _retryDelay = Math.Min(_retryDelay * 2, 60);
            MainFile.Logger.Warn($"[Bilibili] {status}");
        }

        if (MainThread.IsMainThread)
            Apply();
        else
            MainThread.Post(Apply);
    }

    private void StartSocket(int generation, BilibiliApi.DanmuServerInfo info, BilibiliApi.LoginInfo login)
    {
        if (generation != _generation)
            return;

        _connectInFlight = false;
        _retryDelay = ReconnectDelaySeconds;
        _realRoomId = info.RealRoomId;
        _loggedIn = login.IsLogin;
        _ownUid = login.IsLogin ? login.Uid : 0;
        LoginText = login.IsLogin
            ? AiLoc.T("live.login.as", "已登录：{Name}", ("Name", login.UserName))
            : AiLoc.T("live.login.anonymous", "未登录（只能接收弹幕）");

        var host = info.Hosts[_hostIndex % info.Hosts.Count];
        var auth = BilibiliLiveClient.BuildAuthPacket(info.RealRoomId, _ownUid, info.Token, BilibiliApi.Buvid3);

        var client = new BilibiliLiveClient();
        client.MessageReceived += OnDanmakuReceived;
        client.StatusChanged += OnClientStatus;
        client.Disconnected += OnClientDisconnected;
        client.RoomEntered += OnRoomEntered;
        _client = client;
        client.Connect(host.Host, host.Port, auth, _ownUid);
        StatusText = client.Status;
    }

    private void OnClientStatus(string status)
    {
        StatusText = status;
    }

    /// <summary>RitsuLib toast once the room handshake succeeded. 连接直播间成功后的弹窗提醒。</summary>
    private void OnRoomEntered()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;

        if (_time - _lastConnectedToastAt < ConnectedToastCooldownSeconds)
            return;

        _lastConnectedToastAt = _time;

        var room = BilibiliApi.NormalizeRoomInput(AiSettingsStore.Current.BilibiliRoomId);
        if (room.Length == 0)
            room = _realRoomId.ToString();

        var body = _loggedIn
            ? AiLoc.T("live.toast.connected.logged_in",
                "已进入直播间 {Room}，AI 可以回复观众弹幕。", ("Room", room))
            : AiLoc.T("live.toast.connected.anonymous",
                "已进入直播间 {Room}，当前仅接收弹幕（未登录）。", ("Room", room));

        RitsuToastService.ShowInfo(body, AiLoc.T("live.toast.connected.title", "B 站直播间已连接"));
        MainFile.Logger.Info($"[Bilibili] 已连接直播间 {room}");
    }

    private void OnClientDisconnected(string reason)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;

        _client = null;
        _hostIndex++;
        StatusText = reason;
        _reconnectAt = _time + ReconnectDelaySeconds;

        // 连续断开说明 token / 服务器列表可能已失效：下一次重连重新握手，单次断开则复用缓存。
        if (++_disconnectStreak >= 2)
            InvalidateHandshake();
    }

    private void OnDanmakuReceived(DanmakuMessage message)
    {
        _overlay?.Enqueue(message);

        if (message.IsSelf || message.Kind == DanmakuKind.Gift)
            return;

        if (_recentForAi.Count >= 40)
            _recentForAi.RemoveAt(0);
        _recentForAi.Add(message);
    }

    private void StopClient(string status)
    {
        _generation++;
        _connectInFlight = false;
        _reconnectAt = -1;
        _retryDelay = ReconnectDelaySeconds;
        _disconnectStreak = 0;
        InvalidateHandshake();

        var client = _client;
        _client = null;
        if (client != null)
        {
            client.MessageReceived -= OnDanmakuReceived;
            client.StatusChanged -= OnClientStatus;
            client.Disconnected -= OnClientDisconnected;
            client.RoomEntered -= OnRoomEntered;
            client.Dispose();
        }

        StatusText = status;
        _overlay?.Clear();
    }

    // ---- AI replies to live chat ----

    private void PumpAiChat(double delta)
    {
        if (_aiCooldown > 0)
            _aiCooldown -= delta;

        var settings = AiSettingsStore.Current;
        if (!settings.EnableDanmakuAiChat || _aiBusy || _aiCooldown > 0
            || !_loggedIn || _client is not { IsOpen: true } || _recentForAi.Count == 0)
            return;

        var batch = _recentForAi
            .Where(message => DateTime.UtcNow - message.Timestamp < TimeSpan.FromMinutes(3))
            .TakeLast(Math.Clamp(settings.DanmakuAiChatBatchSize, 1, 20))
            .ToList();
        _recentForAi.Clear();

        if (batch.Count == 0)
            return;

        var roomId = _realRoomId;
        // Prompt construction touches game state and localization; do it here on the main thread.
        var gameContext = BuildLiveGameContext();
        _aiBusy = true;
        _aiCooldown = Math.Max(3, settings.DanmakuAiChatCooldownMs / 1000.0);

        _ = Task.Run(async () =>
        {
            try
            {
                await ReplyToDanmakuAsync(batch, roomId, gameContext);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Bilibili] AI 弹幕回复失败: {ex.Message}");
            }
            finally
            {
                MainThread.Post(() => _aiBusy = false);
            }
        });
    }

    /// <summary>Short run/combat summary so the AI can answer gameplay questions from chat.</summary>
    private static string BuildLiveGameContext()
    {
        try
        {
            var manager = AiTeammateManager.Instance;
            if (manager == null)
                return "";

            var aiPlayer = manager.AiPlayer;
            if (aiPlayer == null || manager.QueryService == null)
                return "";

            var builder = new ContextBuilder(manager.QueryService);
            var combat = CombatManager.Instance?.DebugOnlyGetState();
            var text = combat is { CurrentSide: CombatSide.Player }
                ? builder.BuildCombatEssentials(aiPlayer)
                : builder.DescribeRun(aiPlayer);
            return PromptTextHelper.Truncate(text, 1200);
        }
        catch
        {
            return "";
        }
    }

    private async Task ReplyToDanmakuAsync(List<DanmakuMessage> batch, long roomId, string gameContext)
    {
        var manager = AiTeammateManager.Instance;
        if (manager == null || !manager.IsInitialized || manager.PersonalityManager == null)
            return;

        var settings = AiSettingsStore.Current;
        // Bilibili caps normal danmaku at 20 characters for most accounts; stay under it even if
        // the on-screen limit is configured higher.
        var limit = Math.Clamp(Math.Min(settings.DanmakuMaxChars, 20), 5, 20);
        var promptBuilder = new PersonalityPromptBuilder(manager.PersonalityManager);
        var systemPrompt = promptBuilder.BuildSystemPrompt(chatMode: true)
                           + "\n" + AiLoc.T("prompt.live.system",
                               "You are streaming this game to a live audience. The lines below are Bilibili live-room danmaku from viewers. Reply like a streamer chatting with chat: one short, natural sentence in the same language the viewers use (usually Chinese). Never mention being an AI, never explain reasoning, and never use markdown.");

        var lines = new StringBuilder();
        foreach (var message in batch)
            lines.AppendLine($"- {message.Name}: {PromptTextHelper.Truncate(message.Text, 80)}");

        var userPrompt = AiLoc.T("prompt.live.user",
            "Recent live-room danmaku:\n{Messages}\n\nWrite exactly one danmaku reply (at most {Limit} characters). Output only the danmaku text, no quotes, no prefix, no explanation.",
            ("Messages", lines.ToString().TrimEnd()), ("Limit", limit.ToString()));

        if (!string.IsNullOrWhiteSpace(gameContext))
        {
            userPrompt = AiLoc.T("prompt.live.state", "Current game state:\n{State}\n\n", ("State", gameContext))
                         + userPrompt;
        }

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.Chat,
            MaxTokens = 256,
            Temperature = 0.85f,
            FrequencyPenalty = 0.4f,
            PresencePenalty = 0.2f
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var response = await manager.BackendManager.SendAsync(request, timeout.Token);
        if (!response.IsSuccess)
            return;

        var text = SanitizeDanmaku(response.Content, limit);
        if (text.Length == 0)
            return;

        var (success, error) = await BilibiliApi.SendDanmakuAsync(roomId, text, timeout.Token);
        var senderName = AiNames.DisplayName;

        MainThread.Post(() =>
        {
            if (success)
            {
                _overlay?.Enqueue(new DanmakuMessage
                {
                    Name = senderName,
                    Text = text,
                    Kind = DanmakuKind.Local,
                    IsSelf = true
                });
                StatusText = AiLoc.T("live.status.ai_sent", "AI 已发送弹幕：{Text}", ("Text", text));
                MainFile.Logger.Info($"[Bilibili] AI 弹幕已发送: {text}");
                // Optional read-aloud of the AI's own danmaku (viewer danmaku is never read).
                TtsService.Instance.SpeakDanmaku(text);
            }
            else
            {
                StatusText = AiLoc.T("live.status.ai_send_failed", "AI 弹幕发送失败：{Message}",
                    ("Message", error));
                MainFile.Logger.Warn($"[Bilibili] AI 弹幕发送失败: {error}");
            }
        });
    }

    private static string SanitizeDanmaku(string raw, int limit)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text[..^3];
        }

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        text = text.Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』');
        text = text.Replace("AI:", "").Replace("AI：", "").Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        if (text.Length > limit)
        {
            var cut = limit;
            if (char.IsHighSurrogate(text[cut - 1]))
                cut--;
            text = text[..cut];
        }

        return text.Trim();
    }
}
