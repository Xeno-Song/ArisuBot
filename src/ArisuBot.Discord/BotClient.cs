using ArisuBot.Discord.Handlers;
using ArisuBot.Discord.Options;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ArisuBot.Discord;

/// <summary>Discord Bot 생명주기를 관리하는 IHostedService 구현체.</summary>
[ExcludeFromCodeCoverage(Justification = "Discord.Net WebSocket 실연결 필요. E2E 테스트 대상.")]
public class BotClient : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly MessageHandler _messageHandler;
    private readonly SlashCommandHandler _slashCommandHandler;
    private readonly IServiceProvider _services;
    private readonly DiscordOptions _options;
    private readonly ILogger<BotClient> _logger;

    public BotClient(
        DiscordSocketClient client,
        InteractionService interactions,
        MessageHandler messageHandler,
        SlashCommandHandler slashCommandHandler,
        IServiceProvider services,
        IOptions<DiscordOptions> options,
        ILogger<BotClient> logger)
    {
        _client = client;
        _interactions = interactions;
        _messageHandler = messageHandler;
        _slashCommandHandler = slashCommandHandler;
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>봇 이벤트 등록, 커맨드 모듈 로드, Discord 로그인 및 연결 시작.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += _messageHandler.HandleAsync;
        _client.InteractionCreated += _slashCommandHandler.HandleAsync;
        _client.Log += OnLogAsync;

        // 현재 어셈블리의 InteractionModuleBase 구현체 자동 등록
        await _interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), _services);

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();
    }

    /// <summary>Discord 연결 종료 및 로그아웃.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    /// <summary>봇 준비 완료 시 슬래시 커맨드를 개발 길드에 즉시 등록한다.</summary>
    private async Task OnReadyAsync()
    {
        if (ulong.TryParse(_options.DevGuildId, out var guildId))
        {
            await _interactions.RegisterCommandsToGuildAsync(guildId);
            _logger.LogInformation("Slash commands registered to guild {GuildId}", guildId);
        }
        else
        {
            _logger.LogWarning("DevGuildId is not set or invalid. Slash commands not registered.");
        }
    }

    private Task OnLogAsync(LogMessage log)
    {
        var level = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error    => LogLevel.Error,
            LogSeverity.Warning  => LogLevel.Warning,
            LogSeverity.Info     => LogLevel.Information,
            _                    => LogLevel.Debug
        };
        _logger.Log(level, log.Exception, "[Discord] {Message}", log.Message);
        return Task.CompletedTask;
    }
}
