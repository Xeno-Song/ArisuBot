namespace ArisuBot.Core.Options;

/// <summary>유저별 semantic memory 추출 및 압축 설정.</summary>
public class SemanticMemoryOptions
{
    public const string SectionName = "SemanticMemory";

    /// <summary>snapshot의 trait 항목 수가 이 값 이상이면 압축 실행.</summary>
    public int TraitCompressionThreshold { get; set; } = 20;

    /// <summary>snapshot의 episodic 항목 수가 이 값 이상이면 압축 실행.</summary>
    public int EpisodicCompressionThreshold { get; set; } = 20;

    /// <summary>
    /// [Phase 2 예약] 압축 LLM 모델 지정. 현재 미사용.
    /// ILLMProvider.GenerateAsync에 optional model 파라미터 추가 후 적용 예정.
    /// </summary>
    public string? CompressionModel { get; set; }
}
