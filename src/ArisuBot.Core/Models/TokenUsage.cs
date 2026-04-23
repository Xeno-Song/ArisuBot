namespace ArisuBot.Core.Models;

/// <summary>단일 LLM 요청에 대한 토큰 사용량 스냅샷.</summary>
public class TokenUsage
{
    /// <summary>입력 토큰 수 (프롬프트 + 히스토리).</summary>
    public int TokensIn { get; set; }

    /// <summary>출력 토큰 수 (생성된 응답).</summary>
    public int TokensOut { get; set; }

    /// <summary>입력 토큰 중 implicit cache 히트 수.</summary>
    public int TokensCachedIn { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
