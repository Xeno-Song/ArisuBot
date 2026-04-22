namespace ArisuBot.Discord.Options;

/// <summary>Discord Bot 연결 및 동작 설정.</summary>
public class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>Bot 토큰. appsettings.Local.json에서 주입.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>슬래시 커맨드를 즉시 등록할 개발용 길드 ID.</summary>
    public string DevGuildId { get; set; } = string.Empty;

    public MessageListenerOptions MessageListener { get; set; } = new();
}

/// <summary>메시지 리스너 동작 설정.</summary>
public class MessageListenerOptions
{
    /// <summary>모든 메시지에 반응할 채널 ID 목록.</summary>
    public List<ulong> ChannelIds { get; set; } = new();

    /// <summary>봇 멘션 시 채널 목록 외에서도 반응 여부.</summary>
    public bool RespondToMentions { get; set; } = true;
}
