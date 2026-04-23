using Discord.Interactions;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.Discord.Commands;

/// <summary>봇 응답 확인용 ping 커맨드.</summary>
[ExcludeFromCodeCoverage(Justification = "Discord.Net SocketInteractionContext 실연결 필요. E2E 테스트 대상.")]
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
