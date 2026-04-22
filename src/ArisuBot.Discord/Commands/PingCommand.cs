using Discord.Interactions;

namespace ArisuBot.Discord.Commands;

/// <summary>봇 응답 확인용 ping 커맨드.</summary>
public class PingCommand : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>/ping 슬래시 커맨드. 봇 응답 지연 포함 pong 메시지를 반환한다.</summary>
    [SlashCommand("ping", "봇 응답 상태를 확인합니다.")]
    public async Task PingAsync()
    {
        var latency = Context.Client.Latency;
        await RespondAsync($"pong! (latency: {latency}ms)");
    }
}
