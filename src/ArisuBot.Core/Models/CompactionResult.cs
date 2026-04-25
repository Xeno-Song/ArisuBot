using System.Text.Json.Serialization;

namespace ArisuBot.Core.Models;

/// <summary>Compaction LLM 호출로 추출된 단일 사실 항목.</summary>
public record CompactionFact
{
    /// <summary>독립적으로 이해 가능한 사실 문장. 대상(subject)이 포함된 완결형 문장.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>사실의 대상. Discord 사용자명, 채널명, 서버명 등.</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; init; } = string.Empty;

    /// <summary>사실 분류. preference | event | decision | status | relationship | rule</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;
}

/// <summary>Compaction LLM 호출 결과. facts와 summary를 포함한다.</summary>
public record CompactionResult
{
    /// <summary>대화에서 추출된 사실 목록. 없으면 빈 배열.</summary>
    [JsonPropertyName("facts")]
    public List<CompactionFact> Facts { get; init; } = new();

    /// <summary>대화 전반의 맥락 요약 (1–3문장).</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;
}
