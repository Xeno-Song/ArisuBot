namespace ArisuBot.Core.Interfaces;

/// <summary>AI 메시지 입출력 감사 로그 추상화.</summary>
public interface IAIMessageLogger
{
    Task LogAsync(ulong guildId, ulong channelId, ulong userId,
                  string userMessage, string botResponse, string provider,
                  CancellationToken ct = default);
}
