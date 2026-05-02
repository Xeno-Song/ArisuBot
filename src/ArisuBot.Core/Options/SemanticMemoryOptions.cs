namespace ArisuBot.Core.Options;

/// <summary>유저별 semantic memory 추출 및 압축 설정.</summary>
public class SemanticMemoryOptions
{
    public const string SectionName = "SemanticMemory";

    /// <summary>trait 압축 트리거 임계값 (userId당 trait fact 수).</summary>
    public int TraitCompressionThreshold { get; set; } = 20;

    /// <summary>event 압축 트리거 임계값 (userId당 event fact 수).</summary>
    public int EventCompressionThreshold { get; set; } = 20;

    /// <summary>episode 압축 트리거 임계값 (userId당 episode fact 수).</summary>
    public int EpisodeCompressionThreshold { get; set; } = 20;

    /// <summary>
    /// [Phase 2 예약] 압축 LLM 모델 지정. 현재 미사용.
    /// ILLMProvider.GenerateAsync에 optional model 파라미터 추가 후 적용 예정.
    /// </summary>
    public string? CompressionModel { get; set; }
}
