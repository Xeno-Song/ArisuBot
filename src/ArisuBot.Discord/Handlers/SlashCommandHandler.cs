using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ArisuBot.Discord.Handlers;

/// <summary>Discord 슬래시 커맨드 인터랙션을 InteractionService로 라우팅한다.</summary>
public class SlashCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<SlashCommandHandler> _logger;

    public SlashCommandHandler(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        ILogger<SlashCommandHandler> logger)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _logger = logger;
    }

    /// <summary>인터랙션 이벤트 수신 시 호출. 커맨드 컨텍스트를 생성해 실행한다.</summary>
    public async Task HandleAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(_client, interaction);
        var result = await _interactions.ExecuteCommandAsync(context, _services);

        if (!result.IsSuccess)
            _logger.LogWarning("Slash command failed: {Error}", result.ErrorReason);
    }
}
