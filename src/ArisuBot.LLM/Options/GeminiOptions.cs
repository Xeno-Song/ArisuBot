namespace ArisuBot.LLM.Options;

/// <summary>Gemini 공급자 전용 설정. Section: "LLM:Gemini".</summary>
public class GeminiOptions
{
    public const string SectionName = "LLM:Gemini";

    /// <summary>Gemini API 키. 반드시 appsettings.Local.json에서 주입.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>사용할 Gemini 모델 ID.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";
}
