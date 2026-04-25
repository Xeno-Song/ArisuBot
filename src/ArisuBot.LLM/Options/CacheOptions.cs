namespace ArisuBot.LLM.Options;

/// <summary>Gemini 명시적 캐싱 설정. Section: "LLM:Cache".</summary>
public class CacheOptions
{
    public const string SectionName = "LLM:Cache";

    /// <summary>명시적 캐싱 활성화 여부. false이면 기존 캐시도 사용하지 않는다.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>최초 캐시 생성 임계값(토큰). 기존 캐시가 없을 때 누적 비캐시 토큰이 이 값 이상이면 최초 캐시 생성 시도 (velocity gate 추가 적용).</summary>
    public int InitialThresholdTokens { get; set; } = 5000;

    /// <summary>캐시 갱신(rolling) 임계값(토큰). 기존 캐시가 있을 때 누적 비캐시 토큰이 이 값 이상이면 캐시를 교체한다.</summary>
    public int RefreshThresholdTokens { get; set; } = 20000;

    /// <summary>캐시 TTL(초). Gemini API에 전달.</summary>
    public int TtlSeconds { get; set; } = 1800;

    /// <summary>최초 캐시 생성 시 velocity 판단 시간 창(초).</summary>
    public int VelocityWindowSeconds { get; set; } = 180;

    /// <summary>최초 캐시 생성 시 velocity 조건 최소 메시지 수. 미달 시 단발성 대화로 판단해 캐시 생성 억제.</summary>
    public int VelocityMinMessages { get; set; } = 3;
}
