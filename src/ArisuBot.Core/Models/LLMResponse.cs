namespace ArisuBot.Core.Models;

/// <summary>LLM이 생성한 응답. 토큰 사용량은 스트리밍 완료 후 마지막 청크에서 집계.</summary>
public record LLMResponse
{
    public string Content { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>입력 토큰 수 (프롬프트 + 히스토리).</summary>
    public int TokensIn { get; init; }

    /// <summary>출력 토큰 수 (생성된 응답).</summary>
    public int TokensOut { get; init; }

    /// <summary>입력 토큰 중 implicit cache 히트 수.</summary>
    public int TokensCachedIn { get; init; }
}
