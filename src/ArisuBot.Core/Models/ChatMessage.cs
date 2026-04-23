namespace ArisuBot.Core.Models;

/// <summary>단일 대화 메시지.</summary>
public record ChatMessage
{
    public Role Role { get; init; }
    public string Content { get; init; } = string.Empty;
    /// <summary>메시지 발신자 표시 이름. Role.User 메시지에만 설정. LLM 입력 포맷 및 mention 해석에 사용.</summary>
    public string? SenderName { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>메시지 발화자 역할.</summary>
public enum Role
{
    System,
    User,
    Assistant
}
