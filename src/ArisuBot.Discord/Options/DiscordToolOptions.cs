namespace ArisuBot.Discord.Options;

/// <summary>Discord 툴 관련 설정. Section: "Discord:Tools".</summary>
public class DiscordToolOptions
{
    public const string SectionName = "Discord:Tools";

    /// <summary>유저 타임아웃 최소 지속 시간(초). LLM이 이 값 미만을 요청하면 오류 반환.</summary>
    public int TimeoutMinSeconds { get; set; } = 60;

    /// <summary>유저 타임아웃 최대 지속 시간(초). Discord 최대 = 28일(2,419,200초).</summary>
    public int TimeoutMaxSeconds { get; set; } = 604800;
}
