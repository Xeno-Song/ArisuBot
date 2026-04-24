namespace ArisuBot.LLM.Options;

/// <summary>LLM 공통 설정. Section: "LLM".</summary>
public class LLMOptions
{
    public const string SectionName = "LLM";

    /// <summary>사용할 LLM 공급자 이름. 현재: "Gemini".</summary>
    public string Provider { get; set; } = "Gemini";

    /// <summary>최대 출력 토큰 수.</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>생성 온도. 0.0–1.0.</summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>툴 호출 루프 최대 반복 횟수. 무한 루프 방지.</summary>
    public int MaxToolIterations { get; set; } = 5;
}
