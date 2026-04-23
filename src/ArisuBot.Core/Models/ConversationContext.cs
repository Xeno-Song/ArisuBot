namespace ArisuBot.Core.Models;

/// <summary>유저 또는 채널 단위의 대화 컨텍스트.</summary>
public class ConversationContext
{
    public string Id { get; set; } = string.Empty;
    public ContextType Type { get; set; }
    public ulong TargetId { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    /// <summary>이 세션에서 발생한 LLM 요청별 토큰 사용량 이력.</summary>
    public List<TokenUsage> TokenUsage { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>컨텍스트 범위 구분.</summary>
public enum ContextType
{
    User,
    Channel
}
