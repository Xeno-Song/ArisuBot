namespace ArisuBot.LLM.Options;

/// <summary>Gemini 공급자 전용 설정. Section: "LLM:Gemini".</summary>
public class GeminiOptions
{
    public const string SectionName = "LLM:Gemini";

    /// <summary>Gemini API 키. 반드시 appsettings.Local.json에서 주입.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>사용할 Gemini 모델 ID.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>주 모델 ServerError 재시도 소진 후 전환할 fallback 모델 ID. null이면 fallback 없이 즉시 throw.</summary>
    public string? FallbackModel { get; set; }

    /// <summary>동일 모델 ServerError 재시도 허용 횟수. 소진 후 FallbackModel로 전환. 0이면 즉시 전환.</summary>
    public int StreamRetryCount { get; set; } = 2;

    /// <summary>재시도 간 고정 딜레이(ms). 0이면 즉시 재시도.</summary>
    public int RetryDelayMs { get; set; } = 500;
}
