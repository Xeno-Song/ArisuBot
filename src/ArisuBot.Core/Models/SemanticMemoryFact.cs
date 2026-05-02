namespace ArisuBot.Core.Models;

/// <summary>유저에 대해 추출된 단일 장기 기억 항목.</summary>
public record SemanticMemoryFact
{
    /// <summary>독립적으로 이해 가능한 사실 문장. subject가 포함된 완결형 문장.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>사실 분류: "trait" (성향/선호/습관) | "event" (외부 사건) | "episode" (대화 내 경험담).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>이 fact를 추출한 원본 세션 ID.</summary>
    public string SourceContextId { get; init; } = string.Empty;

    /// <summary>추출 시각 (UTC).</summary>
    public DateTime ExtractedAt { get; init; } = DateTime.UtcNow;
}
