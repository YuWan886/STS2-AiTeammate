namespace AiTeammate.AiTeammateCode.Chat;

public enum ChatMessageType
{
    Normal,
    Suggestion,
    Warning,
    SystemInfo
}

/// <summary>
/// Where an AI line came from. The voice layer uses it to decide what to read aloud: with
/// <c>VoiceChatOnly</c> on (the default) only replies to a player message are spoken, while combat
/// chatter and unprompted small talk stay silent.
/// 台词来源：决定语音是否朗读（默认只读玩家聊天回复）。
/// </summary>
public enum ChatMessageSource
{
    Other,
    Combat,
    Auto,
    ChatReply
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SenderName { get; set; } = "";
    public bool IsAi { get; set; }
    public string Content { get; set; } = "";
    public ChatMessageType Type { get; set; }
    public ChatMessageSource Source { get; set; }
    public List<HighlightedCard> HighlightedCards { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class HighlightedCard
{
    public string CardInstanceId { get; set; } = "";
    public string BriefReason { get; set; } = "";
}
