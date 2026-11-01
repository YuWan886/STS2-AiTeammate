using AiTeammate.AiTeammateCode.Agent;

namespace AiTeammate.AiTeammateCode.Settings;

/// <summary>Scrolling direction of the live-danmaku overlay. 弹幕滚动方向。</summary>
public enum DanmakuDirection
{
    /// <summary>Text enters from the left and moves right. 从左向右。</summary>
    LeftToRight = 0,

    /// <summary>Text enters from the right and moves left (the usual video-danmaku direction). 从右向左。</summary>
    RightToLeft = 1
}

/// <summary>
/// All player-editable settings. Persisted by RitsuLib at <c>SaveScope.Global</c> (one set of
/// values shared by every save slot), which matches the old <c>config.json</c> behaviour.
/// </summary>
public sealed class AiSettings
{
    /// <summary>
    /// Schema version written into <c>settings.json</c>. Files from before the field existed read
    /// back as 0, which is how <see cref="AiSettingsStore"/> knows it may apply one-off default
    /// migrations (a changed default alone never reaches an existing settings file).
    /// 设置文件结构版本；旧文件为 0，用于一次性默认值迁移。
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Version of this settings file. 当前设置文件版本。</summary>
    public int SettingsSchemaVersion { get; set; }

    // ---- AI backend ----

    /// <summary>Active backend name: DeepSeek / OpenAI / Ollama. 当前使用的 AI 后端。</summary>
    public string ActiveBackend { get; set; } = "DeepSeek";

    // Stored as-is in settings.json; the settings page only ever displays them masked.
    public string DeepSeekApiKey { get; set; } = "";
    public string DeepSeekModel { get; set; } = "deepseek-v4-flash";
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4o";
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string OllamaModel { get; set; } = "llama3";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ask DeepSeek-compatible models to emit reasoning. 启用模型思考模式（更慢、更贵）。</summary>
    public bool EnableModelThinking { get; set; }

    // ---- Decision ----

    public int DecisionTimeoutMs { get; set; } = 20000;
    public int ChatTimeoutMs { get; set; } = 10000;
    public int NonCombatRetryCount { get; set; } = 2;

    /// <summary>Put the draw / discard / exhaust pile contents into combat prompts. 战斗提示词中包含牌堆内容。</summary>
    public bool EnablePileDetail { get; set; } = true;

    /// <summary>Let the AI call read-only tools (read_pile / card_info / read_relics …) during combat decisions. 允许 AI 主动读取信息。</summary>
    public bool EnableReadTools { get; set; } = true;

    /// <summary>Same tool access for out-of-combat decisions (map / reward / shop / rest / event). 战斗外决策也允许调用工具。</summary>
    public bool EnableOutOfCombatReadTools { get; set; } = true;

    /// <summary>
    /// Lets the AI capture the current game screen and attach it to the follow-up request.
    /// Only models with image input support can use it. 允许 AI 截取当前游戏画面（仅视觉模型有效）。
    /// </summary>
    public bool EnableScreenshotTool { get; set; }

    // ---- Bilibili live danmaku ----

    /// <summary>Shows the live-room danmaku overlay in game. 在游戏内显示 B 站直播弹幕。</summary>
    public bool EnableBilibiliDanmaku { get; set; }

    /// <summary>Live room id or URL, for example <c>21452505</c>. 直播间房间号或链接。</summary>
    public string BilibiliRoomId { get; set; } = "";

    /// <summary>Longest danmaku text shown on screen (characters). 屏幕上弹幕文字的最大字数。</summary>
    public int DanmakuMaxChars { get; set; } = 50;

    /// <summary>Whether danmaku text enters from the left or the right. 弹幕滚动方向。</summary>
    public DanmakuDirection DanmakuMoveDirection { get; set; } = DanmakuDirection.RightToLeft;

    /// <summary>Scroll speed in pixels per second at a 1080p viewport height. 弹幕速度（1080p 下的像素/秒）。</summary>
    public int DanmakuSpeed { get; set; } = 220;

    /// <summary>Danmaku font size in pixels. 弹幕字号。</summary>
    public int DanmakuFontSize { get; set; } = 20;

    /// <summary>Danmaku text opacity in percent. 弹幕不透明度（百分比）。</summary>
    public int DanmakuOpacity { get; set; } = 100;

    /// <summary>Shows the sender's Bilibili avatar next to the text. 弹幕前显示发送者头像。</summary>
    public bool DanmakuShowAvatar { get; set; } = true;

    /// <summary>In multiplayer, only the host's screen shows the danmaku. 多人模式下只在房主屏幕上显示。</summary>
    public bool DanmakuHostOnly { get; set; } = true;

    /// <summary>Percentage of the screen height used by the danmaku lanes. 弹幕区域占屏幕高度百分比。</summary>
    public int DanmakuAreaPercent { get; set; } = 55;

    // Stored as-is; the settings page only ever displays them masked. 登录 Cookie，仅存储不回显。
    public string BilibiliSessData { get; set; } = "";
    public string BilibiliBiliJct { get; set; } = "";

    /// <summary>Lets the AI reply to live-room danmaku (requires a logged-in Bilibili account). 允许 AI 回复弹幕。</summary>
    public bool EnableDanmakuAiChat { get; set; } = true;

    /// <summary>Minimum time between two AI danmaku replies. 两次 AI 弹幕回复的最小间隔。</summary>
    public int DanmakuAiChatCooldownMs { get; set; } = 30000;

    /// <summary>How many recent danmaku are shown to the AI in one reply request. 每次回复参考的弹幕条数。</summary>
    public int DanmakuAiChatBatchSize { get; set; } = 6;

    // ---- Identity ----

    /// <summary>
    /// Custom display name for the AI teammate. Empty follows the active personality's name.
    /// The AI and dsh may change it during a run, so treat this as "last known nickname".
    /// AI 队友的自定义昵称；为空时沿用当前人格名。
    /// </summary>
    public string AiNickname { get; set; } = "";

    // ---- Personality / memory ----

    public string ActivePersonalityId { get; set; } = "tactician";
    public bool EnablePersonalityGrowth { get; set; } = true;
    public bool EnableLongTermMemory { get; set; } = true;
    public int MaxMemoriesPerPrompt { get; set; } = 5;
    public int MemoryMergeThreshold { get; set; } = 200;

    // ---- Chat ----

    public bool EnableAIChat { get; set; } = true;
    public bool EnableSpeechBubble { get; set; } = true;
    public int ChatCooldownMs { get; set; } = 15000;
    public int MaxChatHistoryForPrompt { get; set; } = 10;

    // ---- Voice (MiniMax TTS) ----

    /// <summary>Reads AI lines aloud through MiniMax text-to-speech. 用 MiniMax 语音朗读 AI 台词。</summary>
    public bool EnableVoice { get; set; }

    /// <summary>MiniMax API key (write-only in the UI, like the backend keys). MiniMax 接口密钥。</summary>
    public string MiniMaxApiKey { get; set; } = "";

    /// <summary>
    /// MiniMax endpoint; the north-China host <c>https://api-bj.minimaxi.com</c> answers faster for
    /// mainland players. 语音接口地址。
    /// </summary>
    public string MiniMaxBaseUrl { get; set; } = "https://api.minimax.cn";

    /// <summary>TTS model id. <c>speech-2.8-turbo</c> is the fast/cheap default. 语音模型。</summary>
    public string VoiceModel { get; set; } = "speech-2.8-turbo";

    /// <summary>Voice id; cloned and designed voices use the same field. 音色 ID。</summary>
    public string VoiceId { get; set; } = "male-qn-qingse";

    /// <summary>Speech speed in percent, 50–200. 语速（百分比）。</summary>
    public int VoiceSpeedPercent { get; set; } = 100;

    /// <summary>Pitch shift, -12..12. 音调。</summary>
    public int VoicePitch { get; set; }

    /// <summary>Emotion for the voice; <c>auto</c> lets the model pick. 情绪。</summary>
    public string VoiceEmotion { get; set; } = "auto";

    /// <summary>
    /// Volume of the dedicated voice bus in dB. The voice no longer rides the game's SFX bus
    /// (which the game attenuates by <c>slider²</c>), and MiniMax output sits around -19.5 LUFS,
    /// so the default is a positive offset rather than 0. 语音音量：独立语音总线的增益（dB）。
    /// </summary>
    public int VoiceVolumeDb { get; set; } = 6;

    /// <summary>
    /// Only read replies to a player chat message; combat chatter and unprompted small talk stay
    /// silent. On by default. 只朗读玩家聊天回复，不读战斗台词。
    /// </summary>
    public bool VoiceChatOnly { get; set; } = true;

    /// <summary>
    /// Also read aloud the short replies the AI posts to a Bilibili live room. Viewer danmaku is
    /// never spoken (a scrolling chat would talk over the game and burn the budget).
    /// 朗读 AI 自己发出的直播弹幕；观众弹幕不读。
    /// </summary>
    public bool VoiceReadDanmaku { get; set; } = true;

    /// <summary>Longest text sent in one synthesis request, in characters. 单次合成最大字数。</summary>
    public int VoiceMaxChars { get; set; } = 120;

    /// <summary>Client-side request cap per minute (MiniMax allows 10 free / 20 paid). 每分钟请求数。</summary>
    public int VoiceRequestsPerMinute { get; set; } = 12;

    /// <summary>Billed-character budget per run, 0 = unlimited. 每局字符预算。</summary>
    public int VoiceBudgetCharsPerRun { get; set; } = 20000;

    /// <summary>Billed-character budget per hour, 0 = unlimited. 每小时字符预算。</summary>
    public int VoiceBudgetCharsPerHour { get; set; } = 8000;

    /// <summary>
    /// Only the host plays voice. Off by default: every client synthesizes its own audio, so the
    /// AI's voice is heard by everyone, at the cost of one request per listening client.
    /// 仅房主播放语音（默认关：每人都能听到）。
    /// </summary>
    public bool VoiceHostOnly { get; set; }

    /// <summary>Cut the previous line when a new one starts. 新台词打断旧台词。</summary>
    public bool VoiceInterrupt { get; set; } = true;

    /// <summary>Cache synthesized audio on disk; a hit costs nothing. 语音缓存。</summary>
    public bool VoiceCacheEnabled { get; set; } = true;

    /// <summary>Maximum cached audio files. 缓存文件上限。</summary>
    public int VoiceCacheMaxFiles { get; set; } = 300;

    /// <summary>Maximum cache size in megabytes. 缓存大小上限（MB）。</summary>
    public int VoiceCacheMaxMegabytes { get; set; } = 30;

    // ---- Solo climb ----

    /// <summary>Lets the AI drive the local player in singleplayer runs. 单人模式由 AI 接管。</summary>
    public bool EnableSoloClimb { get; set; }

    /// <summary>Character id used by the "start AI solo climb" button; empty = random.</summary>
    public string SoloCharacterId { get; set; } = "";

    /// <summary>Ascension level for a new AI solo climb; 0 = no ascension. 单人爬塔的进阶难度。</summary>
    public int SoloAscensionLevel { get; set; }

    // ---- External agent bridge (dsh) ----

    /// <summary>Starts the local TCP bridge an external dsh plugin connects to. 启动外部 Agent 桥接。</summary>
    public bool EnableAgentBridge { get; set; }

    /// <summary>Loopback port the dsh plugin connects to. 外部 Agent 桥接端口。</summary>
    public int AgentBridgePort { get; set; } = 26333;

    /// <summary>Optional request token; empty means any local process may connect. 可选的请求 token。</summary>
    public string AgentBridgeToken { get; set; } = "";

    /// <summary>Allows the external agent to perform actions (not just read state). 允许外部 Agent 执行动作。</summary>
    public bool AgentBridgeAllowActions { get; set; } = true;

    /// <summary>
    /// How long the external agent may stay silent before the AI stops waiting and hands the
    /// decision to its own backend. 0 disables the fallback (keep waiting like before).
    /// dsh 静默多少秒后回退到自带后端；0 = 不回退。
    /// </summary>
    public int AgentFallbackSeconds { get; set; } = 30;

    /// <summary>
    /// Lets the attached dsh conversation answer the in-game chat panel instead of the built-in
    /// backend. Only takes effect while a dsh plugin is polling <c>sts2_chat_poll</c>, so an older
    /// plugin (or one with no bound conversation) keeps the old behaviour. dsh 接管聊天回复。
    /// </summary>
    public bool AgentChatToDsh { get; set; } = true;

    /// <summary>
    /// How long a player chat line waits for dsh before falling back (default two minutes, enough
    /// for a model that is finishing a turn). 等 dsh 回复聊天的秒数。
    /// </summary>
    public int AgentChatReplyTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// When dsh does not answer within the timeout (or disconnects), let the built-in backend reply
    /// instead of leaving the player without an answer. 聊天超时/断线时回退内置后端。
    /// </summary>
    public bool AgentChatFallbackToBackend { get; set; } = true;

    // ---- Auto-launched dsh session ----
    // The mod can start a dsh session by itself when an AI teammate joins a run, so the player no
    // longer has to open a conversation and type "play Slay the Spire 2" by hand.
    // AI 队友入局时自动拉起一个 dsh 会话，省掉手动开对话的步骤。

    /// <summary>Lets dsh take over automatically when an AI teammate joins the run. 自动接管。</summary>
    public bool AutoLaunchAgentSession { get; set; } = true;

    /// <summary>Directory the new conversation belongs to (dsh groups sessions by it). 会话工作区。</summary>
    public string AgentLaunchWorkspace { get; set; } = @"F:\sts2-mod\MiniMods";

    /// <summary>First message of the conversation the plugin opens. 新对话的首条消息。</summary>
    public string AgentLaunchPrompt { get; set; } = AgentTakeoverInvite.DefaultPrompt;

    // ---- Maintenance ----

    public bool AutoRefreshCache { get; set; } = true;
    public bool EnableDecisionLog { get; set; } = true;
    public bool EnableTrafficLog { get; set; }
    public int LogRetention { get; set; } = 5;

    public void ResetToDefaults()
    {
        var defaults = new AiSettings();

        SettingsSchemaVersion = CurrentSchemaVersion;
        ActiveBackend = defaults.ActiveBackend;
        DeepSeekApiKey = defaults.DeepSeekApiKey;
        DeepSeekModel = defaults.DeepSeekModel;
        DeepSeekBaseUrl = defaults.DeepSeekBaseUrl;
        OpenAiApiKey = defaults.OpenAiApiKey;
        OpenAiModel = defaults.OpenAiModel;
        OpenAiBaseUrl = defaults.OpenAiBaseUrl;
        OllamaModel = defaults.OllamaModel;
        OllamaBaseUrl = defaults.OllamaBaseUrl;
        EnableModelThinking = defaults.EnableModelThinking;

        DecisionTimeoutMs = defaults.DecisionTimeoutMs;
        ChatTimeoutMs = defaults.ChatTimeoutMs;
        NonCombatRetryCount = defaults.NonCombatRetryCount;
        EnablePileDetail = defaults.EnablePileDetail;
        EnableReadTools = defaults.EnableReadTools;
        EnableOutOfCombatReadTools = defaults.EnableOutOfCombatReadTools;
        EnableScreenshotTool = defaults.EnableScreenshotTool;

        EnableBilibiliDanmaku = defaults.EnableBilibiliDanmaku;
        BilibiliRoomId = defaults.BilibiliRoomId;
        DanmakuMaxChars = defaults.DanmakuMaxChars;
        DanmakuMoveDirection = defaults.DanmakuMoveDirection;
        DanmakuSpeed = defaults.DanmakuSpeed;
        DanmakuFontSize = defaults.DanmakuFontSize;
        DanmakuOpacity = defaults.DanmakuOpacity;
        DanmakuShowAvatar = defaults.DanmakuShowAvatar;
        DanmakuHostOnly = defaults.DanmakuHostOnly;
        DanmakuAreaPercent = defaults.DanmakuAreaPercent;
        BilibiliSessData = defaults.BilibiliSessData;
        BilibiliBiliJct = defaults.BilibiliBiliJct;
        EnableDanmakuAiChat = defaults.EnableDanmakuAiChat;
        DanmakuAiChatCooldownMs = defaults.DanmakuAiChatCooldownMs;
        DanmakuAiChatBatchSize = defaults.DanmakuAiChatBatchSize;

        AiNickname = defaults.AiNickname;

        ActivePersonalityId = defaults.ActivePersonalityId;
        EnablePersonalityGrowth = defaults.EnablePersonalityGrowth;
        EnableLongTermMemory = defaults.EnableLongTermMemory;
        MaxMemoriesPerPrompt = defaults.MaxMemoriesPerPrompt;
        MemoryMergeThreshold = defaults.MemoryMergeThreshold;

        EnableAIChat = defaults.EnableAIChat;
        EnableSpeechBubble = defaults.EnableSpeechBubble;
        ChatCooldownMs = defaults.ChatCooldownMs;
        MaxChatHistoryForPrompt = defaults.MaxChatHistoryForPrompt;

        EnableVoice = defaults.EnableVoice;
        MiniMaxApiKey = defaults.MiniMaxApiKey;
        MiniMaxBaseUrl = defaults.MiniMaxBaseUrl;
        VoiceModel = defaults.VoiceModel;
        VoiceId = defaults.VoiceId;
        VoiceSpeedPercent = defaults.VoiceSpeedPercent;
        VoicePitch = defaults.VoicePitch;
        VoiceEmotion = defaults.VoiceEmotion;
        VoiceVolumeDb = defaults.VoiceVolumeDb;
        VoiceChatOnly = defaults.VoiceChatOnly;
        VoiceReadDanmaku = defaults.VoiceReadDanmaku;
        VoiceMaxChars = defaults.VoiceMaxChars;
        VoiceRequestsPerMinute = defaults.VoiceRequestsPerMinute;
        VoiceBudgetCharsPerRun = defaults.VoiceBudgetCharsPerRun;
        VoiceBudgetCharsPerHour = defaults.VoiceBudgetCharsPerHour;
        VoiceHostOnly = defaults.VoiceHostOnly;
        VoiceInterrupt = defaults.VoiceInterrupt;
        VoiceCacheEnabled = defaults.VoiceCacheEnabled;
        VoiceCacheMaxFiles = defaults.VoiceCacheMaxFiles;
        VoiceCacheMaxMegabytes = defaults.VoiceCacheMaxMegabytes;

        EnableSoloClimb = defaults.EnableSoloClimb;
        SoloCharacterId = defaults.SoloCharacterId;
        SoloAscensionLevel = defaults.SoloAscensionLevel;

        EnableAgentBridge = defaults.EnableAgentBridge;
        AgentBridgePort = defaults.AgentBridgePort;
        AgentBridgeToken = defaults.AgentBridgeToken;
        AgentBridgeAllowActions = defaults.AgentBridgeAllowActions;
        AgentFallbackSeconds = defaults.AgentFallbackSeconds;
        AgentChatToDsh = defaults.AgentChatToDsh;
        AgentChatReplyTimeoutSeconds = defaults.AgentChatReplyTimeoutSeconds;
        AgentChatFallbackToBackend = defaults.AgentChatFallbackToBackend;
        AutoLaunchAgentSession = defaults.AutoLaunchAgentSession;
        AgentLaunchWorkspace = defaults.AgentLaunchWorkspace;
        AgentLaunchPrompt = defaults.AgentLaunchPrompt;

        AutoRefreshCache = defaults.AutoRefreshCache;
        EnableDecisionLog = defaults.EnableDecisionLog;
        EnableTrafficLog = defaults.EnableTrafficLog;
        LogRetention = defaults.LogRetention;
    }
}
