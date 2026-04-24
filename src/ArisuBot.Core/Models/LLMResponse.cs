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

    /// <summary>이 LLM 응답 생성 과정에서 실행된 Tool 호출 이력. ToolCall/ToolResponse 순서로 교대 저장.</summary>
    public IReadOnlyList<ChatMessage> ToolCallHistory { get; init; } = [];

    /// <summary>
    /// Provider별 보조 메타데이터 JSON. Gemini의 thought_signature 등 LLMResponse.Content(텍스트)에
    /// 담을 수 없는 model 응답 Parts 보존용. MessageHandler가 Assistant ChatMessage 저장 시 함께 영속화해
    /// 다음 호출의 BuildContents에서 model Content.Parts로 복원 → cache hit 및 thinking context 유지.
    /// </summary>
    public string? ProviderMetadataJson { get; init; }
}
