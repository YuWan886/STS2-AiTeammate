using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Settings;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Ui.Toast;

namespace AiTeammate.AiTeammateCode.Voice;

/// <summary>
/// MiniMax TTS orchestration: listens to the chat transcript, decides what may be spoken, cleans and
/// splits the text, applies the rate limit / character budget / failure breaker, consults the disk
/// cache and hands the audio to <see cref="TtsPlayer"/> on the main thread.
///
/// Hard rules:
/// <list type="bullet">
///   <item>Voice never blocks gameplay or decisions — everything runs fire-and-forget.</item>
///   <item>A failure is logged, never surfaced as a chat error; three in a row disable voice for the session.</item>
///   <item>Only AI <c>Normal</c> lines are read; with <c>VoiceChatOnly</c> (default) only chat replies are.</item>
/// </list>
/// 语音服务：只朗读 AI 台词，限流限预算、失败静默、永不阻塞游戏。
/// </summary>
public sealed class TtsService
{
    public static TtsService Instance { get; } = new();

    /// <summary>At most this many lines may wait for synthesis; newer lines win.</summary>
    private const int MaxPending = 3;

    /// <summary>A long reply is read as at most this many requests (billing is per character).</summary>
    private const int MaxSegmentsPerLine = 2;

    private const int FailuresBeforeDisable = 3;

    private readonly SemaphoreSlim _synthesisGate = new(1, 1);
    private readonly object _stateLock = new();

    // ---- runtime state ----
    private int _pending;
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private DateTime _hourStartUtc = DateTime.UtcNow;
    private long _hourChars;
    private long _runChars;
    private int _consecutiveFailures;
    private bool _disabledForSession;
    private string _lastError = "";
    private string _lastSpokenText = "";
    private DateTime _lastSpokenUtc = DateTime.MinValue;

    /// <summary>Id of the newest chat line that was accepted for speech; older lines are dropped.</summary>
    private string _activeLineId = "";
    private bool _attached;
    private bool _runHookAttempted;
    private bool _runHooked;

    public bool SessionDisabled
    {
        get { lock (_stateLock) return _disabledForSession; }
    }

    public string LastError
    {
        get { lock (_stateLock) return _lastError; }
    }

    public long RunCharacters
    {
        get { lock (_stateLock) return _runChars; }
    }

    /// <summary>
    /// Starts listening to chat messages. Idempotent; called once from
    /// <see cref="AiTeammateManager.Initialize"/>.
    /// </summary>
    public void Attach(ChatManager chat)
    {
        if (_attached)
            return;

        _attached = true;
        chat.OnMessageReceived += OnMessageReceived;
        MainThread.Post(TryHookRunManager);
        MainFile.Logger.Info("[VOICE] 语音服务已接入聊天事件");
    }

    private void TryHookRunManager()
    {
        if (_runHooked || _runHookAttempted && RunManager.Instance == null)
            return;

        try
        {
            var run = RunManager.Instance;
            if (run == null)
                return;

            run.RunStarted += OnRunStarted;
            _runHooked = true;
            _runHookAttempted = true;
            MainFile.Logger.Info("[VOICE] 已挂接 RunStarted（新对局重置语音预算）");
        }
        catch (Exception ex)
        {
            _runHookAttempted = true;
            MainFile.Logger.Warn($"[VOICE] 挂接 RunStarted 失败: {ex.Message}");
        }
    }

    private void OnRunStarted(RunState state)
    {
        lock (_stateLock)
        {
            _runChars = 0;
            _disabledForSession = false;
            _consecutiveFailures = 0;
        }

        MainFile.Logger.Info("[VOICE] 新对局：语音预算已重置");
    }

    private void OnMessageReceived(ChatMessage message)
    {
        try
        {
            if (!message.IsAi || message.Type != ChatMessageType.Normal)
                return;

            if (_disabledForSession)
                return;

            var settings = AiSettingsStore.Current;
            if (!settings.EnableVoice)
                return;
            if (string.IsNullOrWhiteSpace(settings.MiniMaxApiKey))
                return;
            if (settings.VoiceChatOnly && message.Source != ChatMessageSource.ChatReply)
                return;

            // Multiplayer cost control: every client runs its own AI chat, so "host only" keeps the
            // billed requests to one per line.
            if (settings.VoiceHostOnly && RunManager.Instance?.NetService?.Type == NetGameType.Client)
                return;

            Speak(message.Content, message.Id, settings);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 处理台词失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Speaks text that never enters the chat transcript — today the short replies the AI posts to
    /// a Bilibili live room. Viewer danmaku is deliberately never spoken: reading a scrolling chat
    /// would talk over the game and burn the character budget. Everything else (limiter, budget,
    /// cache, breaker, line grouping) is the chat path, and <see cref="AiSettings.VoiceReadDanmaku"/>
    /// gates it. 朗读聊天面板之外的台词（目前只有 AI 自己发的直播弹幕）。
    /// </summary>
    public void SpeakDanmaku(string text)
    {
        try
        {
            if (_disabledForSession)
                return;

            var settings = AiSettingsStore.Current;
            if (!settings.EnableVoice || !settings.VoiceReadDanmaku)
                return;
            if (string.IsNullOrWhiteSpace(settings.MiniMaxApiKey))
                return;
            if (settings.VoiceHostOnly && RunManager.Instance?.NetService?.Type == NetGameType.Client)
                return;

            Speak(text, "danmaku-" + Guid.NewGuid().ToString("N"), settings);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 弹幕朗读失败: {ex.Message}");
        }
    }

    /// <summary>Shared body of every spoken source: clean, dedupe within 15 s, split, enqueue.</summary>
    private void Speak(string raw, string lineId, AiSettings settings)
    {
        if (!_runHooked)
            MainThread.Post(TryHookRunManager);

        var text = TtsTextNormalizer.Clean(raw,
            TtsTextNormalizer.SupportsInterjections(settings.VoiceModel));
        if (text.Length < 2)
            return;

        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(text, _lastSpokenText, StringComparison.Ordinal) &&
                now - _lastSpokenUtc < TimeSpan.FromSeconds(15))
                return;

            _lastSpokenText = text;
            _lastSpokenUtc = now;
        }

        var maxChars = Math.Clamp(settings.VoiceMaxChars, 20, 300);
        var segments = TtsTextNormalizer.Split(text, maxChars);
        if (segments.Count == 0)
            return;

        _activeLineId = lineId;
        for (var i = 0; i < segments.Count && i < MaxSegmentsPerLine; i++)
            Enqueue(segments[i], lineId);
    }

    private void Enqueue(string text, string lineId)
    {
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SynthesizeAndPlayAsync(text, lineId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[VOICE] 合成任务异常: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    private async Task SynthesizeAndPlayAsync(string text, string lineId)
    {
        var settings = AiSettingsStore.Current;
        var options = BuildOptions(settings, text);
        var key = TtsAudioCache.Key(options, text);

        if (settings.VoiceCacheEnabled && TtsAudioCache.TryGet(key, out var cached))
        {
            if (IsSuperseded(settings, lineId))
                return;

            MainFile.Logger.Info($"[VOICE] 缓存命中 {Snippet(text)}");
            PlayAudio(cached, settings, lineId);
            return;
        }

        await _synthesisGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ThrottleAsync(settings).ConfigureAwait(false);

            var estimate = TtsTextNormalizer.EstimateBilledChars(text);
            if (!TryReserveBudget(settings, estimate))
            {
                MainFile.Logger.Info("[VOICE] 本局/本小时语音预算已用尽，本条只显示文字");
                return;
            }

            var result = await MiniMaxTtsClient.SynthesizeAsync(text, options, CancellationToken.None)
                .ConfigureAwait(false);

            if (!result.Ok)
            {
                AdjustBudget(-estimate);
                RecordFailure(result.Error);
                return;
            }

            AdjustBudget(result.UsageCharacters - estimate);
            RecordSuccess();
            MainFile.Logger.Info(
                $"[VOICE] 合成完成 {Snippet(text)} duration={result.AudioLengthMs}ms chars={result.UsageCharacters}");

            if (result.Audio is not { Length: > 0 })
            {
                RecordFailure("empty audio payload");
                return;
            }

            if (settings.VoiceCacheEnabled)
                TtsAudioCache.Put(key, result.Audio, settings.VoiceCacheMaxFiles, settings.VoiceCacheMaxMegabytes);

            // A slow request can finish after a newer line already started: don't let the stale
            // segment cut the newer voice. (With interrupt off everything is queued anyway.)
            if (IsSuperseded(settings, lineId))
                return;

            PlayAudio(result.Audio, settings, lineId);
        }
        finally
        {
            _synthesisGate.Release();
        }
    }

    /// <summary>Synthesizes and plays the settings-page sample line. Returns a human-readable result.</summary>
    public async Task<(bool Ok, string Message)> PreviewAsync(CancellationToken ct)
    {
        var settings = AiSettingsStore.Current;
        if (string.IsNullOrWhiteSpace(settings.MiniMaxApiKey))
            return (false, AiLoc.T("voice.test.no_key", "设置里还没有 MiniMax API Key。"));

        var text = TtsTextNormalizer.Clean(AiLoc.T("voice.test_line",
            "你好，我是你的 AI 队友。这一局，就交给我吧。"),
            TtsTextNormalizer.SupportsInterjections(settings.VoiceModel));
        if (text.Length < 2)
            text = "你好，我是你的 AI 队友。";

        var options = BuildOptions(settings, text);
        var key = TtsAudioCache.Key(options, text);
        var previewLine = "preview-" + Guid.NewGuid().ToString("N");

        if (settings.VoiceCacheEnabled && TtsAudioCache.TryGet(key, out var cached))
        {
            PlayAudio(cached, settings, previewLine);
            return (true, AiLoc.T("voice.test.ok", "正在播放试听语音。"));
        }

        await _synthesisGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await MiniMaxTtsClient.SynthesizeAsync(text, options, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                lock (_stateLock) _lastError = result.Error;
                return (false, AiLoc.T("voice.test.failed", "试听失败：{Error}", ("Error", result.Error)));
            }

            if (result.Audio is { Length: > 0 })
            {
                if (settings.VoiceCacheEnabled)
                    TtsAudioCache.Put(key, result.Audio, settings.VoiceCacheMaxFiles, settings.VoiceCacheMaxMegabytes);
                PlayAudio(result.Audio, settings, previewLine);
            }

            return (true, AiLoc.T("voice.test.ok", "正在播放试听语音。"));
        }
        finally
        {
            _synthesisGate.Release();
        }
    }

    /// <summary>Refreshes the selectable voice list from MiniMax.</summary>
    public async Task<(bool Ok, string Message)> FetchVoicesAsync(CancellationToken ct)
    {
        var settings = AiSettingsStore.Current;
        var baseUrl = string.IsNullOrWhiteSpace(settings.MiniMaxBaseUrl)
            ? MiniMaxTtsClient.DefaultBaseUrl
            : settings.MiniMaxBaseUrl;

        var (ok, error, voices) = await MiniMaxTtsClient.ListVoicesAsync(baseUrl, settings.MiniMaxApiKey, ct)
            .ConfigureAwait(false);
        if (!ok)
            return (false, AiLoc.T("voice.fetch.failed", "拉取音色失败：{Error}", ("Error", error)));

        TtsVoiceCatalog.Replace(voices);
        return (true, AiLoc.T("voice.fetch.ok", "已获取 {Count} 个音色，可在下拉框里选择。",
            ("Count", voices.Count.ToString())));
    }

    /// <summary>Status line for the settings page: usage, rough cost, cache size, last error.</summary>
    public string DescribeStatus()
    {
        try
        {
            var settings = AiSettingsStore.Current;
            long runChars;
            long hourChars;
            string lastError;
            bool disabled;

            lock (_stateLock)
            {
                EnsureHourWindowLocked();
                runChars = _runChars;
                hourChars = _hourChars;
                lastError = _lastError;
                disabled = _disabledForSession;
            }

            var (files, bytes) = TtsAudioCache.Stats();
            var cost = EstimateCostYuan(runChars, settings.VoiceModel);

            var status = AiLoc.T("settings.voice.status",
                "本局 {Chars} 字符（≈¥{Cost}，本小时 {Hour}）· 缓存 {Files} 条 / {Size} · 最近错误：{Error}",
                ("Chars", runChars.ToString("N0")),
                ("Cost", cost.ToString("0.00")),
                ("Hour", hourChars.ToString("N0")),
                ("Files", files.ToString()),
                ("Size", FormatSize(bytes)),
                ("Error", string.IsNullOrWhiteSpace(lastError)
                    ? AiLoc.T("settings.voice.status.none", "无")
                    : lastError));

            if (disabled)
                status = AiLoc.T("settings.voice.status.disabled", "本次游戏已自动关闭语音（连续失败）· ") + status;

            return status;
        }
        catch (Exception ex)
        {
            return $"[VOICE] {ex.Message}";
        }
    }

    /// <summary>Stops whatever is speaking (mute toggle, run end).</summary>
    public void StopAll()
    {
        try { TtsPlayer.StopAll(); }
        catch (Exception ex) { MainFile.Logger.Warn($"[VOICE] 停止播放失败: {ex.Message}"); }
    }

    private void PlayAudio(byte[] audio, AiSettings settings, string lineId)
    {
        var interrupt = settings.VoiceInterrupt;
        MainThread.Post(() =>
        {
            try
            {
                // Volume lives on the dedicated voice bus, so playback only needs the line identity.
                TtsPlayer.Play(audio, interrupt, lineId);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[VOICE] 播放失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// True when a newer chat line has been accepted for speech while this one was still being
    /// synthesized. Only meaningful with interrupt on; otherwise the queue plays everything.
    /// </summary>
    private bool IsSuperseded(AiSettings settings, string lineId) =>
        settings.VoiceInterrupt && !string.Equals(lineId, _activeLineId, StringComparison.Ordinal);

    private static TtsSynthOptions BuildOptions(AiSettings settings, string text) => new(
        string.IsNullOrWhiteSpace(settings.MiniMaxBaseUrl)
            ? MiniMaxTtsClient.DefaultBaseUrl
            : settings.MiniMaxBaseUrl.Trim(),
        settings.MiniMaxApiKey.Trim(),
        string.IsNullOrWhiteSpace(settings.VoiceModel) ? MiniMaxTtsClient.DefaultModel : settings.VoiceModel.Trim(),
        string.IsNullOrWhiteSpace(settings.VoiceId) ? MiniMaxTtsClient.DefaultVoiceId : settings.VoiceId.Trim(),
        Math.Clamp(settings.VoiceSpeedPercent, 50, 200),
        Math.Clamp(settings.VoicePitch, -12, 12),
        string.IsNullOrWhiteSpace(settings.VoiceEmotion) ? "auto" : settings.VoiceEmotion.Trim(),
        TtsTextNormalizer.DetectLanguageBoost(text));

    /// <summary>Reserves an estimated character budget before the request; adjusted afterwards.</summary>
    private bool TryReserveBudget(AiSettings settings, int estimate)
    {
        lock (_stateLock)
        {
            EnsureHourWindowLocked();

            if (settings.VoiceBudgetCharsPerRun > 0 && _runChars + estimate > settings.VoiceBudgetCharsPerRun)
                return false;
            if (settings.VoiceBudgetCharsPerHour > 0 && _hourChars + estimate > settings.VoiceBudgetCharsPerHour)
                return false;

            _runChars += estimate;
            _hourChars += estimate;
            return true;
        }
    }

    private void AdjustBudget(int delta)
    {
        if (delta == 0)
            return;

        lock (_stateLock)
        {
            _runChars = Math.Max(0, _runChars + delta);
            _hourChars = Math.Max(0, _hourChars + delta);
        }
    }

    private void EnsureHourWindowLocked()
    {
        if (DateTime.UtcNow - _hourStartUtc < TimeSpan.FromHours(1))
            return;

        _hourStartUtc = DateTime.UtcNow;
        _hourChars = 0;
    }

    /// <summary>Spaces requests so the account never hits MiniMax's T2A RPM limit (10 free / 20 paid).</summary>
    private async Task ThrottleAsync(AiSettings settings)
    {
        var rpm = Math.Clamp(settings.VoiceRequestsPerMinute, 1, 20);
        var minGap = TimeSpan.FromSeconds(60.0 / rpm);

        TimeSpan wait;
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            var earliest = _lastRequestUtc + minGap;
            if (earliest > now)
            {
                wait = earliest - now;
                _lastRequestUtc = earliest;
            }
            else
            {
                wait = TimeSpan.Zero;
                _lastRequestUtc = now;
            }
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait).ConfigureAwait(false);
    }

    private void RecordFailure(string error)
    {
        lock (_stateLock)
        {
            _lastError = error;
            _consecutiveFailures++;
        }

        MainFile.Logger.Warn($"[VOICE] 合成失败: {error}");
        AiTeammateManager.Instance?.AiLog.Log("VOICE", $"合成失败: {error}");

        bool disable;
        lock (_stateLock)
            disable = _consecutiveFailures >= FailuresBeforeDisable && !_disabledForSession;

        if (!disable)
            return;

        lock (_stateLock)
            _disabledForSession = true;

        MainFile.Logger.Warn($"[VOICE] 连续 {FailuresBeforeDisable} 次失败，本次游戏自动关闭语音");
        MainThread.Post(() =>
        {
            try
            {
                RitsuToastService.ShowError(AiLoc.T("voice.disabled_after_failures",
                    "语音已暂停：连续合成失败（{Error}）。可在设置里重新开启。", ("Error", error)));
            }
            catch
            {
                // A toast failure must never break the settings/chat path.
            }
        });
    }

    private void RecordSuccess()
    {
        lock (_stateLock)
            _consecutiveFailures = 0;
    }

    private static double EstimateCostYuan(long chars, string model)
    {
        // speech-2.8-turbo / 2.6-turbo / 02-turbo: ¥2 per 10k billed characters; hd: ¥3.5.
        var perTenThousand = model.Contains("turbo", StringComparison.OrdinalIgnoreCase) ? 2.0 : 3.5;
        return chars / 10000.0 * perTenThousand;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB"
            : (bytes / 1024.0).ToString("0") + " KB";

    private static string Snippet(string text) =>
        text.Length <= 24 ? text : text[..24] + "…";
}

/// <summary>Session cache of the voices reported by MiniMax (settings drop-down).</summary>
public static class TtsVoiceCatalog
{
    private static readonly object Sync = new();
    private static List<TtsVoiceInfo> _voices = new();

    public static IReadOnlyList<TtsVoiceInfo> All
    {
        get { lock (Sync) return _voices.ToList(); }
    }

    public static void Replace(IEnumerable<TtsVoiceInfo> voices)
    {
        lock (Sync)
        {
            _voices = voices
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                .GroupBy(v => v.VoiceId, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(v => v.VoiceId, StringComparer.Ordinal)
                .ToList();
        }
    }
}
