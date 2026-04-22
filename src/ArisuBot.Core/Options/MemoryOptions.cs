namespace ArisuBot.Core.Options;

/// <summary>대화 컨텍스트 최대 메시지 수 설정.</summary>
public class MemoryOptions
{
    public const string SectionName = "Memory";
    public int UserContextMaxMessages { get; set; } = 20;
    public int ChannelContextMaxMessages { get; set; } = 50;
}
