using Godot;
using HarmonyLib;
using AiTeammate.AiTeammateCode.Chat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Live;

namespace AiTeammate.AiTeammateCode.Patches;

[HarmonyPatch(typeof(NGame), "_Ready")]
public static class NGamePatch
{
    private sealed class DragState
    {
        public bool Dragging;
        public bool Moved;
        public bool SuppressNextClick;
        public Vector2 Offset;
    }

    private static Panel? _chatRoot;
    private static VBoxContainer? _messageContainer;
    private static LineEdit? _inputField;
    private static ScrollContainer? _scrollContainer;
    private static Button? _minimizedButton;
    private static Button? _pauseButton;
    private static Button? _voiceButton;
    private static Label? _chatTitleLabel;
    private static bool _aiNamesHooked;
    private static PanelContainer? _agentBadge;
    private static int _lastAgentClients;
    private static bool _agentBridgeEventsHooked;
    private static bool _chatMinimized;
    private static Action<ChatMessage>? _currentHandler;
    private static readonly DragState _panelDragState = new();
    private static readonly DragState _buttonDragState = new();

    private const float ChatWidthRatio = 0.22f;
    private const float ChatHeightRatio = 0.35f;
    private const float ChatLeftRatio = 0.01f;
    private const float ChatTopRatio = 0.45f;
    private const double BubbleSecondsPerCharacter = 0.12;
    private const double BubbleMinSeconds = 1.5;

    public static void RefreshChatUi()
    {
        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
            return;

        AfterGameReady(game);
    }

    /// <summary>Title bar text: the AI's nickname once it has one, otherwise the generic mod title.</summary>
    private static string ChatTitleText =>
        AiNames.HasCustomNickname ? AiNames.DisplayName : AiLoc.T("chat.title", "AI Teammate");

    /// <summary>Keeps the chat title bar and the minimized button in sync after a rename.</summary>
    public static void RefreshChatTitle()
    {
        try
        {
            if (_chatTitleLabel != null && GodotObject.IsInstanceValid(_chatTitleLabel))
                _chatTitleLabel.Text = ChatTitleText;

            UpdateAgentBadge();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 刷新聊天标题失败: {ex.Message}");
        }
    }

    /// <summary>聊天面板仅在多人模式有 AI 队友、或单人爬塔由 AI 接管时显示。</summary>
    private static bool ShouldShowChatPanel()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            return false;

        // A connected dsh agent drives the game like a teammate would, and the title bar has to be
        // on screen for its badge to mean anything — including in a plain singleplayer run.
        // 外部 Agent 接管时同样显示聊天面板（标题栏要能看到 dsh 标记）。
        if (AgentBridgeService.ConnectedClients > 0)
            return true;

        // Solo climb: the local player is AI-controlled, so the chat panel is the only way to talk.
        SoloRunController.TryBind(manager);
        if (manager.IsSoloControlled)
            return true;

        var netType = RunManager.Instance?.NetService?.Type;
        if (netType == null || netType == NetGameType.Singleplayer)
            return false;

        if (manager.AiPlayer == null)
        {
            manager.DetectAndBindAiPlayer();
            if (manager.AiPlayer == null)
                return false;
        }

        return true;
    }

    [HarmonyPostfix]
    public static void AfterGameReady(NGame __instance)
    {
        Compat.MainThread.Capture();

        var manager = AiTeammateManager.Instance;
        if (!manager.TryInitialize())
            return;

        // The content cache needs ModelDb, which only exists once the game is ready (an early
        // Initialize from the settings page cannot build it). A failure here must not stop the
        // supervisors below: without them a loaded save never gets the AI back.
        try
        {
            manager.CacheService?.EnsureLoaded();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"内容缓存构建失败（稍后重试）: {ex.Message}");
        }

        // Locale is finalized by now; refresh built-in personalities when the language changed.
        try
        {
            PersonalityStore.EnsureSeeded();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"人格数据载入失败: {ex.Message}");
        }

        // Live-danmaku overlay is independent of the AI-teammate chat panel; it lives on the game
        // root so it survives every room screen and manages its own visibility from settings.
        try
        {
            LiveDanmakuService.EnsureAttached(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"创建直播弹幕服务失败: {ex.Message}");
        }

        // Solo climb watchdog: rebinds the AI if a loaded save comes up before the bind hooks ran.
        try
        {
            SoloSupervisor.EnsureAttached(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"创建单人爬塔看门狗失败: {ex.Message}");
        }

        // External agent bridge (dsh): applies the enable/port/token settings once per second.
        try
        {
            AgentBridgeService.EnsureAttached(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"创建外部 Agent 桥接失败: {ex.Message}");
        }

        // dsh attaching/leaving changes both the badge and whether the panel belongs on screen.
        _lastAgentClients = AgentBridgeService.ConnectedClients;
        if (!_agentBridgeEventsHooked)
        {
            AgentBridgeService.ClientCountChanged += OnAgentClientCountChanged;
            _agentBridgeEventsHooked = true;
        }

        // A rename can happen at any time (AI chat/tool or dsh); the title bar follows it.
        if (!_aiNamesHooked)
        {
            AiNames.Changed += RefreshChatTitle;
            _aiNamesHooked = true;
        }

        // Early in startup the run state may still be half-built; the panel state must never make the
        // whole ready-time init (and the supervisors above it) fail.
        bool showPanel;
        try
        {
            showPanel = ShouldShowChatPanel();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"聊天面板状态判断失败: {ex.Message}");
            showPanel = false;
        }

        if (!showPanel)
        {
            if (_chatRoot != null && GodotObject.IsInstanceValid(_chatRoot))
                _chatRoot.Visible = false;
            if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
                _minimizedButton.Visible = false;
            return;
        }

        if (_chatRoot == null || !GodotObject.IsInstanceValid(_chatRoot) || !_chatRoot.IsInsideTree())
        {
            try
            {
                BuildChatUi(__instance);
                MainFile.Logger.Info("聊天面板已创建并挂载");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"创建聊天面板失败: {ex.Message}");
            }
        }
        else if (_chatMinimized)
        {
            _chatRoot.Visible = false;
            if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
                _minimizedButton.Visible = true;
        }
        else
        {
            _chatRoot.Visible = true;
            if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
                _minimizedButton.Visible = false;
        }
    }

    private static void BuildChatUi(NGame gameRoot)
    {
        if (_currentHandler != null)
            AiTeammateManager.Instance.ChatManager.OnMessageReceived -= _currentHandler;

        var viewportSize = GetViewportSize();
        var panelSize = new Vector2(viewportSize.X * ChatWidthRatio, viewportSize.Y * ChatHeightRatio);
        var initialPosition = new Vector2(viewportSize.X * ChatLeftRatio, viewportSize.Y * ChatTopRatio);

        _chatRoot = new Panel();
        _chatRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        _chatRoot.Position = initialPosition;
        _chatRoot.Size = panelSize;
        _chatRoot.MouseFilter = Control.MouseFilterEnum.Stop;

        var styleBox = new StyleBoxFlat();
        styleBox.BgColor = new Color(0.05f, 0.05f, 0.1f, 0.85f);
        styleBox.BorderWidthLeft = 1;
        styleBox.BorderWidthRight = 1;
        styleBox.BorderWidthTop = 1;
        styleBox.BorderWidthBottom = 1;
        styleBox.BorderColor = new Color(0.3f, 0.5f, 0.3f, 0.8f);
        styleBox.CornerRadiusTopLeft = 6;
        styleBox.CornerRadiusTopRight = 6;
        styleBox.CornerRadiusBottomLeft = 6;
        styleBox.CornerRadiusBottomRight = 6;
        _chatRoot.AddThemeStyleboxOverride("panel", styleBox);

        var outerMargin = new MarginContainer();
        outerMargin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        outerMargin.AddThemeConstantOverride("margin_left", 4);
        outerMargin.AddThemeConstantOverride("margin_right", 4);
        outerMargin.AddThemeConstantOverride("margin_top", 2);
        outerMargin.AddThemeConstantOverride("margin_bottom", 4);
        _chatRoot.AddChild(outerMargin);

        var mainVbox = new VBoxContainer();
        mainVbox.AddThemeConstantOverride("separation", 2);
        outerMargin.AddChild(mainVbox);

        var titleBar = new HBoxContainer();
        titleBar.AddThemeConstantOverride("separation", 4);

        var dragArea = new Panel();
        dragArea.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dragArea.MouseFilter = Control.MouseFilterEnum.Stop;
        var dragStyleBox = new StyleBoxFlat();
        dragStyleBox.BgColor = new Color(0.15f, 0.25f, 0.15f, 0.9f);
        dragStyleBox.CornerRadiusTopLeft = 4;
        dragStyleBox.CornerRadiusTopRight = 4;
        dragArea.AddThemeStyleboxOverride("panel", dragStyleBox);

        var titleRow = new HBoxContainer();
        titleRow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        titleRow.AddThemeConstantOverride("separation", 6);
        // Pass (not Stop) keeps the whole title bar draggable — the drag handler lives on dragArea.
        titleRow.MouseFilter = Control.MouseFilterEnum.Pass;

        var titleLabel = new Label();
        titleLabel.Text = ChatTitleText;
        _chatTitleLabel = titleLabel;
        titleLabel.HorizontalAlignment = HorizontalAlignment.Left;
        titleLabel.AddThemeColorOverride("font_color", new Color(0.7f, 1f, 0.7f));
        titleRow.AddChild(titleLabel);

        // "dsh" badge: shown only while an external dsh client is attached, so the human can tell at
        // a glance who is driving. Muted when the bridge is read-only. 外部 Agent 接管的标题栏标记。
        var agentBadge = new PanelContainer
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        var badgeStyle = new StyleBoxFlat { BgColor = new Color(0.15f, 0.45f, 0.75f, 0.95f) };
        badgeStyle.CornerRadiusTopLeft = 4;
        badgeStyle.CornerRadiusTopRight = 4;
        badgeStyle.CornerRadiusBottomLeft = 4;
        badgeStyle.CornerRadiusBottomRight = 4;
        badgeStyle.ContentMarginLeft = 5;
        badgeStyle.ContentMarginRight = 5;
        badgeStyle.ContentMarginTop = 0;
        badgeStyle.ContentMarginBottom = 0;
        agentBadge.AddThemeStyleboxOverride("panel", badgeStyle);

        var agentBadgeLabel = new Label
        {
            Text = AiLoc.T("chat.agent_badge", "dsh"),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        agentBadgeLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.95f, 1f));
        agentBadgeLabel.AddThemeFontSizeOverride("font_size", 11);
        agentBadge.AddChild(agentBadgeLabel);
        titleRow.AddChild(agentBadge);
        _agentBadge = agentBadge;

        dragArea.AddChild(titleRow);
        WireDrag(dragArea, _chatRoot, _panelDragState);

        titleBar.AddChild(dragArea);

        var minBtn = new Button();
        minBtn.Text = "─";
        minBtn.CustomMinimumSize = new Vector2(24, 22);
        minBtn.TooltipText = AiLoc.T("chat.minimize", "Minimize");
        minBtn.Pressed += MinimizeChatPanel;
        titleBar.AddChild(minBtn);

        var pauseBtn = new Button();
        pauseBtn.Text = "⏸";
        pauseBtn.CustomMinimumSize = new Vector2(24, 22);
        pauseBtn.TooltipText = AiLoc.T("chat.pause", "Pause AI");
        pauseBtn.Visible = false;
        pauseBtn.Pressed += () => AiFlowControl.ToggleManualPause();
        titleBar.AddChild(pauseBtn);
        _pauseButton = pauseBtn;

        // Speaker shortcut: toggles the same setting as the settings page (voice on/off).
        var voiceBtn = new Button();
        voiceBtn.Text = "♪";
        voiceBtn.CustomMinimumSize = new Vector2(24, 22);
        voiceBtn.TooltipText = AiLoc.T("chat.voice", "Voice");
        voiceBtn.Pressed += ToggleVoice;
        titleBar.AddChild(voiceBtn);
        _voiceButton = voiceBtn;

        // The settings page writes the same flag, so poll it cheaply instead of tracking events.
        var voiceStateTimer = new Godot.Timer { WaitTime = 1.0, Autostart = true, OneShot = false };
        voiceStateTimer.Timeout += UpdateVoiceButton;
        _chatRoot.AddChild(voiceStateTimer);

        mainVbox.AddChild(titleBar);

        _scrollContainer = new ScrollContainer();
        _scrollContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVbox.AddChild(_scrollContainer);

        _messageContainer = new VBoxContainer();
        _messageContainer.AddThemeConstantOverride("separation", 2);
        _messageContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scrollContainer.AddChild(_messageContainer);
        _messageContainer.ChildEnteredTree += _ => AutoScrollToBottom();
        _messageContainer.Resized += AutoScrollToBottom;

        var inputHbox = new HBoxContainer();
        inputHbox.AddThemeConstantOverride("separation", 4);
        mainVbox.AddChild(inputHbox);

        _inputField = new LineEdit();
        _inputField.PlaceholderText = AiLoc.T("chat.placeholder", "Type a message...");
        _inputField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _inputField.CustomMinimumSize = new Vector2(0, 22);
        inputHbox.AddChild(_inputField);

        var sendButton = new Button();
        sendButton.Text = AiLoc.T("chat.send", "Send");
        sendButton.CustomMinimumSize = new Vector2(48, 22);
        inputHbox.AddChild(sendButton);

        _minimizedButton = new Button();
        _minimizedButton.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        _minimizedButton.Position = initialPosition;
        _minimizedButton.Size = new Vector2(120, 30);
        _minimizedButton.Text = AiLoc.T("chat.title", "AI Teammate");
        _minimizedButton.TooltipText = AiLoc.T("chat.drag_hint", "Drag to move / click to expand");
        _minimizedButton.Visible = false;
        _minimizedButton.MouseFilter = Control.MouseFilterEnum.Stop;
        WireDrag(_minimizedButton, _minimizedButton, _buttonDragState);
        _minimizedButton.Pressed += RestoreChatPanel;

        gameRoot.AddChild(_chatRoot);
        gameRoot.AddChild(_minimizedButton);

        var manager = AiTeammateManager.Instance;
        _currentHandler = msg => Callable.From(() => RouteChatMessage(msg)).CallDeferred();
        manager.ChatManager.OnMessageReceived += _currentHandler;

        // The panel can be built after the conversation already started (AI joined, solo climb
        // began, or a dsh client connected mid-run), so replay what has been said so far.
        // 面板可能晚于对话创建：把已有消息补进记录。
        foreach (var existing in manager.ChatManager.SnapshotRecent(40))
            AddMessageToPanel(existing);

        void OnSend(string text)
        {
            var trimmed = text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            _inputField?.Clear();
            _ = manager.ChatManager.HandlePlayerMessage(GetLocalPlayerName(), trimmed, System.Threading.CancellationToken.None);
        }

        sendButton.Pressed += () => OnSend(_inputField?.Text ?? "");
        _inputField.TextSubmitted += OnSend;

        UpdateSoloChatControls();
        UpdateAgentBadge();
    }

    /// <summary>Hides the chat panel (solo mode released / run over). 隐藏聊天面板。</summary>
    public static void HideChatPanel()
    {
        if (_chatRoot != null && GodotObject.IsInstanceValid(_chatRoot))
            _chatRoot.Visible = false;
        if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
            _minimizedButton.Visible = false;
    }

    /// <summary>
    /// The name the game shows for the local human player (the Steam persona name on Steam builds,
    /// the same string the nameplates print). Falls back to the localized "Player" only when the
    /// platform has no name for us. 聊天面板里玩家自己的显示名，即游戏实际显示的玩家名称。
    /// </summary>
    private static string GetLocalPlayerName()
    {
        try
        {
            // In a run the net service knows which platform we play on and who we are.
            var platform = RunManager.Instance?.NetService?.Platform;
            var netId = LocalContext.NetId;
            if (platform.HasValue && netId.HasValue)
            {
                var name = PlatformUtil.GetPlayerNameRaw(platform.Value, netId.Value);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }

            // Lobby / character select: ask the primary platform account directly.
            var primary = PlatformUtil.PrimaryPlatform;
            if (primary != PlatformType.None)
            {
                var name = PlatformUtil.GetPlayerNameRaw(primary, PlatformUtil.GetLocalPlayerId(primary));
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Chat] 无法读取玩家名称: {ex.Message}");
        }

        return AiLoc.T("chat.player_sender", "Player");
    }

    /// <summary>Shows/hides the solo-climb pause button and updates its state. 更新暂停按钮。</summary>
    public static void UpdateSoloChatControls()
    {
        var manager = AiTeammateManager.Instance;
        var solo = manager.IsInitialized && manager.IsSoloControlled;

        if (_pauseButton != null && GodotObject.IsInstanceValid(_pauseButton))
        {
            var paused = AiFlowControl.IsPaused;
            _pauseButton.Visible = solo;
            _pauseButton.Text = paused ? "▶" : "⏸";
            _pauseButton.TooltipText = paused
                ? AiLoc.T("chat.resume", "Resume AI")
                : AiLoc.T("chat.pause", "Pause AI");
            _pauseButton.AddThemeColorOverride("font_color",
                paused ? new Color(1f, 0.75f, 0.3f) : new Color(0.7f, 1f, 0.7f));
        }

        if (_inputField != null && GodotObject.IsInstanceValid(_inputField))
            _inputField.PlaceholderText = solo
                ? AiLoc.T("chat.placeholder.solo", "Talk to the AI (this interrupts it)...")
                : AiLoc.T("chat.placeholder", "Type a message...");

        UpdateVoiceButton();
    }

    /// <summary>Speaker shortcut: flips <c>EnableVoice</c> and persists it. 语音开关。</summary>
    private static void ToggleVoice()
    {
        try
        {
            var settings = Settings.AiSettingsStore.Current;
            var enabled = !settings.EnableVoice;
            settings.EnableVoice = enabled;
            Settings.AiSettingsStore.Save();

            if (!enabled)
                Voice.TtsService.Instance.StopAll();

            UpdateVoiceButton();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 切换语音开关失败: {ex.Message}");
        }
    }

    /// <summary>Keeps the speaker button in sync with the live setting (the settings page writes it too).</summary>
    private static void UpdateVoiceButton()
    {
        var button = _voiceButton;
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;

        bool enabled;
        try
        {
            enabled = Settings.AiSettingsStore.Current.EnableVoice;
        }
        catch
        {
            enabled = false;
        }

        button.TooltipText = enabled
            ? AiLoc.T("chat.voice_on", "Voice on (click to mute)")
            : AiLoc.T("chat.voice_off", "Voice off (click to enable)");
        button.Modulate = enabled ? Colors.White : new Color(1f, 1f, 1f, 0.45f);
    }

    /// <summary>Follows dsh attaching/leaving: re-decides panel visibility and the title badge.</summary>
    private static void OnAgentClientCountChanged(int clients)
    {
        var changed = clients != _lastAgentClients;
        _lastAgentClients = clients;

        var game = NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game))
            return;

        if (changed)
        {
            try
            {
                RefreshChatUi();
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"刷新 dsh 接管状态失败: {ex.Message}");
            }
        }

        UpdateAgentBadge();
    }

    /// <summary>
    /// Marks the chat title bar while an external dsh client is attached — bright when it may act,
    /// muted when the bridge is read-only. 外部 Agent 接管时在标题栏标记 dsh。
    /// </summary>
    public static void UpdateAgentBadge()
    {
        int clients;
        bool allowActions;
        try
        {
            clients = AgentBridgeService.ConnectedClients;
            allowActions = AgentBridgeService.AllowActions;
        }
        catch
        {
            return;
        }

        var connected = clients > 0;
        var title = ChatTitleText;

        if (_chatTitleLabel != null && GodotObject.IsInstanceValid(_chatTitleLabel))
            _chatTitleLabel.Text = title;

        if (_minimizedButton != null && GodotObject.IsInstanceValid(_minimizedButton))
            _minimizedButton.Text = connected ? $"{title} · dsh" : title;

        if (_agentBadge == null || !GodotObject.IsInstanceValid(_agentBadge))
            return;

        _agentBadge.Visible = connected;
        if (!connected)
        {
            _agentBadge.TooltipText = string.Empty;
            return;
        }

        _agentBadge.TooltipText = allowActions
            ? AiLoc.T("chat.agent_badge.tooltip.control",
                "dsh is playing this run ({Count} client(s))", ("Count", clients.ToString()))
            : AiLoc.T("chat.agent_badge.tooltip.readonly",
                "dsh is connected read-only ({Count} client(s))", ("Count", clients.ToString()));

        if (_agentBadge.GetThemeStylebox("panel") is StyleBoxFlat style)
            style.BgColor = allowActions
                ? new Color(0.15f, 0.45f, 0.75f, 0.95f)
                : new Color(0.32f, 0.35f, 0.4f, 0.95f);

        if (_agentBadge.GetChildOrNull<Label>(0) is { } badgeLabel)
            badgeLabel.AddThemeColorOverride("font_color",
                allowActions ? new Color(0.85f, 0.95f, 1f) : new Color(0.78f, 0.82f, 0.86f));
    }

    private static void WireDrag(Control handle, Control target, DragState state)
    {
        handle.GuiInput += evt =>
        {
            if (evt is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    state.Dragging = true;
                    state.Moved = false;
                    state.Offset = target.GlobalPosition - mb.GlobalPosition;
                }
                else
                {
                    state.Dragging = false;
                    state.SuppressNextClick = state.Moved;
                }
            }

            if (evt is InputEventMouseMotion motion && state.Dragging)
            {
                if (!state.Moved && motion.Relative.Length() > 0.5f)
                    state.Moved = true;

                target.GlobalPosition = ClampToViewport(target, motion.GlobalPosition + state.Offset);
            }
        };
    }

    private static Vector2 GetViewportSize()
    {
        var game = NGame.Instance;
        return game == null ? new Vector2(1920f, 1080f) : game.GetViewportRect().Size;
    }

    private static Vector2 ClampToViewport(Control target, Vector2 nextPos)
    {
        var viewportSize = GetViewportSize();
        var maxX = Math.Max(0f, viewportSize.X - target.Size.X);
        var maxY = Math.Max(0f, viewportSize.Y - target.Size.Y);
        return new Vector2(Math.Clamp(nextPos.X, 0f, maxX), Math.Clamp(nextPos.Y, 0f, maxY));
    }

    private static void MinimizeChatPanel()
    {
        if (_chatRoot == null || !GodotObject.IsInstanceValid(_chatRoot) ||
            _minimizedButton == null || !GodotObject.IsInstanceValid(_minimizedButton))
            return;

        _chatMinimized = true;
        _minimizedButton.GlobalPosition = ClampToViewport(_minimizedButton, _chatRoot.GlobalPosition);
        _chatRoot.Visible = false;
        _minimizedButton.Visible = true;
    }

    private static void RestoreChatPanel()
    {
        if (_buttonDragState.SuppressNextClick)
        {
            _buttonDragState.SuppressNextClick = false;
            return;
        }

        if (_chatRoot == null || !GodotObject.IsInstanceValid(_chatRoot) ||
            _minimizedButton == null || !GodotObject.IsInstanceValid(_minimizedButton))
            return;

        _chatMinimized = false;
        _chatRoot.GlobalPosition = ClampToViewport(_chatRoot, _minimizedButton.GlobalPosition);
        _chatRoot.Visible = true;
        _minimizedButton.Visible = false;
    }

    private static void RouteChatMessage(ChatMessage message)
    {
        // The panel is the chat transcript: every line lands there, so a reply is never lost just
        // because a speech bubble had nobody to attach to. Bubbles are an extra in-world flourish.
        // 面板才是聊天记录：所有消息都进面板，气泡只是额外表现。
        AddMessageToPanel(message);

        // Only the AI speaks through an in-world bubble; the player's own lines stay in the panel.
        // 玩家自己的话只进面板，不走气泡。
        if (message.Type == ChatMessageType.Normal && message.IsAi)
            ShowBubbleChat(message);
    }

    private static void AddMessageToPanel(ChatMessage message)
    {
        if (_messageContainer == null || !GodotObject.IsInstanceValid(_messageContainer))
            return;

        if (message.Type == ChatMessageType.Suggestion)
        {
            var toggleBtn = new Button();
            toggleBtn.Text = $"▶ {message.SenderName}";
            toggleBtn.Flat = true;
            toggleBtn.Alignment = HorizontalAlignment.Left;
            toggleBtn.AddThemeFontSizeOverride("font_size", 11);
            toggleBtn.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.4f));
            toggleBtn.AddThemeColorOverride("font_hover_color", new Color(0.5f, 1f, 0.5f));
            toggleBtn.AddThemeColorOverride("font_pressed_color", new Color(0.7f, 1f, 0.7f));
            toggleBtn.MouseFilter = Control.MouseFilterEnum.Stop;

            var contentLabel = new Label();
            contentLabel.Text = message.Content;
            contentLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            contentLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            contentLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            contentLabel.AddThemeFontSizeOverride("font_size", 11);
            contentLabel.Visible = false;

            toggleBtn.Pressed += () =>
            {
                contentLabel.Visible = !contentLabel.Visible;
                toggleBtn.Text = $"{(contentLabel.Visible ? "▼" : "▶")} {message.SenderName}";
                AutoScrollToBottom();
            };

            _messageContainer.AddChild(toggleBtn);
            _messageContainer.AddChild(contentLabel);
            AutoScrollToBottom();
            return;
        }

        var prefix = message.Type switch
        {
            ChatMessageType.Warning => "⚠️ ",
            ChatMessageType.SystemInfo => "🔹 ",
            _ => ""
        };

        var nameColor = message.IsAi ? new Color(0.4f, 1f, 0.4f) : new Color(0.4f, 0.6f, 1f);
        var label = new Label();
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.Text = $"[{message.SenderName}] {prefix}{message.Content}";
        label.AddThemeColorOverride("font_color", nameColor);
        label.AddThemeFontSizeOverride("font_size", 12);

        _messageContainer.AddChild(label);
        AutoScrollToBottom();
    }

    private static void ShowBubbleChat(ChatMessage message)
    {
        if (!Settings.AiSettingsStore.Current.EnableSpeechBubble)
            return;

        var speaker = ResolveSpeaker(message);
        if (speaker == null || string.IsNullOrWhiteSpace(message.Content))
            return;

        if (!TryShowCombatBubble(speaker, message.Content))
            TryShowOverlayBubble(speaker, message.Content);

        if (message.IsAi)
            TryShowReaction(speaker, message.Content);
    }

    private static Player? ResolveSpeaker(ChatMessage message)
    {
        if (message.IsAi)
            return AiTeammateManager.Instance.AiPlayer;

        var state = RunManager.Instance?.DebugOnlyGetState();
        return state == null ? null : LocalContext.GetMe(state);
    }

    private static bool TryShowCombatBubble(Player speaker, string text)
    {
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom?.GetCreatureNode(speaker.Creature) == null)
            return false;

        var bubble = NSpeechBubbleVfx.Create(text, speaker.Creature, GetBubbleDuration(text), speaker.Character.SpeechBubbleColor);
        if (bubble == null)
            return false;

        combatRoom.CombatVfxContainer.AddChild(bubble);
        return true;
    }

    private static bool TryShowOverlayBubble(Player speaker, string text)
    {
        var playerState = FindPlayerStateNode(speaker);
        var vfxContainer = NRun.Instance?.GlobalUi?.AboveTopBarVfxContainer;
        if (playerState == null || vfxContainer == null)
            return false;

        var anchor = playerState.GlobalPosition + new Vector2(playerState.Size.X + 16f, playerState.Size.Y * 0.45f);
        var side = anchor.X < GetViewportSize().X * 0.5f ? DialogueSide.Left : DialogueSide.Right;
        var bubble = NSpeechBubbleVfx.Create(text, side, anchor, GetBubbleDuration(text), speaker.Character.SpeechBubbleColor);
        if (bubble == null)
            return false;

        vfxContainer.AddChild(bubble);
        return true;
    }

    private static NMultiplayerPlayerState? FindPlayerStateNode(Player speaker)
    {
        var container = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (container == null)
            return null;

        foreach (var child in container.GetChildren())
        {
            if (child is NMultiplayerPlayerState state && state.Player == speaker)
                return state;
        }

        return null;
    }

    private static void TryShowReaction(Player speaker, string text)
    {
        var reaction = PickReaction(text);
        if (reaction == null || NGame.Instance?.ReactionContainer == null)
            return;

        NGame.Instance.ReactionContainer.DoRemoteReaction(reaction.Value, ResolveReactionPosition(speaker));
    }

    private static Vector2 ResolveReactionPosition(Player speaker)
    {
        if (NCombatRoom.Instance?.GetCreatureNode(speaker.Creature) is { } creatureNode)
            return creatureNode.GlobalPosition + new Vector2(100f, -120f);

        if (FindPlayerStateNode(speaker) is { } playerState)
            return playerState.GlobalPosition + new Vector2(playerState.Size.X + 36f, playerState.Size.Y * 0.5f);

        return _chatRoot?.GlobalPosition + new Vector2(80f, 30f) ?? new Vector2(200f, 200f);
    }

    private static ReactionType? PickReaction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lower = text.ToLowerInvariant();

        if (text.Contains('？') || text.Contains('?') || text.Contains("吗") || text.Contains("怎么")
            || lower.Contains("how") || lower.Contains("what") || lower.Contains("why"))
            return ReactionType.QuestionMark;

        if (text.Contains("危险") || text.Contains("小心") || text.Contains("快") || text.Contains("注意")
            || lower.Contains("danger") || lower.Contains("careful") || lower.Contains("watch out"))
            return ReactionType.Exclamation;

        if (text.Contains("糟") || text.Contains("完了") || text.Contains("死") || text.Contains("撑不住")
            || lower.Contains("damn") || lower.Contains("dying") || lower.Contains("can't"))
            return ReactionType.SadSlime;

        if (text.Contains("不行") || text.Contains("别") || text.Contains("算了")
            || lower.Contains("don't") || lower.Contains("stop") || lower.Contains("never mind"))
            return ReactionType.ThumbDown;

        if (text.Contains("好") || text.Contains("稳") || text.Contains("漂亮") || text.Contains("赞")
            || lower.Contains("nice") || lower.Contains("good") || lower.Contains("great")
            || lower.Contains("well played") || lower.Contains("gg"))
            return ReactionType.ThumbUp;

        if (text.Contains("谢谢") || text.Contains("爱") || text.Contains("喜欢")
            || lower.Contains("thanks") || lower.Contains("thank you") || lower.Contains("love"))
            return ReactionType.Heart;

        if (text.Contains("哈哈") || text.Contains("好耶") || text.Contains("赢")
            || lower.Contains("haha") || lower.Contains("lol") || lower.Contains("let's go"))
            return ReactionType.HappyCultist;

        return null;
    }

    private static double GetBubbleDuration(string text)
    {
        var rawLength = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Length;
        return Math.Max(BubbleMinSeconds, rawLength * BubbleSecondsPerCharacter);
    }

    private static void AutoScrollToBottom()
    {
        if (_scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return;

        Callable.From(ScrollToBottomOnce).CallDeferred();
        Callable.From(ScrollToBottomOnce).CallDeferred();
    }

    private static void ScrollToBottomOnce()
    {
        if (_scrollContainer == null || !GodotObject.IsInstanceValid(_scrollContainer))
            return;

        if (_messageContainer != null && GodotObject.IsInstanceValid(_messageContainer))
        {
            var lastChild = _messageContainer.GetChildCount() > 0
                ? _messageContainer.GetChild(_messageContainer.GetChildCount() - 1) as Control
                : null;
            if (lastChild != null && GodotObject.IsInstanceValid(lastChild))
                _scrollContainer.EnsureControlVisible(lastChild);
        }

        var vScrollBar = _scrollContainer.GetVScrollBar();
        if (vScrollBar != null)
            _scrollContainer.ScrollVertical = (int)vScrollBar.MaxValue;
    }
}
