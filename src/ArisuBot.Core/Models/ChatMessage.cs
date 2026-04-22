namespace ArisuBot.Core.Models;

/// <summary>단일 대화 메시지.</summary>
public record ChatMessage
{
    public Role Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>메시지 발화자 역할.</summary>
public enum Role
{
    System,
    User,
    Assistant
}
