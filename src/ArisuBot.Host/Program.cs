using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using ArisuBot.Discord;
using ArisuBot.Discord.Handlers;
using ArisuBot.Discord.Options;
using ArisuBot.Discord.Services;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using ArisuBot.Infrastructure.Prompts;
using ArisuBot.LLM.Extensions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Bot 진입점. DI 구성 및 호스트 시작.
var host = Host.CreateDefaultBuilder(args)
    // 실행 파일 위치를 content root로 설정 — appsettings.json이 복사된 bin/Debug/net8.0/ 탐색
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false);
        // appsettings.Local.json: 시크릿 설정. 존재하지 않으면 건너뜀.
        config.AddJsonFile("appsettings.Local.json", optional: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddLog4Net("log4net.config");
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        // Options 바인딩
        services.Configure<DiscordOptions>(config.GetSection(DiscordOptions.SectionName));
        services.Configure<MongoDbOptions>(config.GetSection(MongoDbOptions.SectionName));
        services.Configure<MemoryOptions>(config.GetSection(MemoryOptions.SectionName));

        // Infrastructure — MongoDB
        services.AddSingleton<MongoDbContext>();
        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IAIMessageLogger, AIMessageLogger>();

        // Infrastructure — Prompts (봇 시작 시 파일 로드, /new-session으로 재로드)
        services.AddSingleton<IPromptLoader>(_ =>
            new FilePromptLoader(Path.Combine(AppContext.BaseDirectory, "prompts")));

        // Core
        services.AddSingleton<ConversationService>();

        // LLM
        services.AddLLMProvider(config);

        // Discord — MessageContent intent: 개발자 포털에서 수동 활성화 필요
        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                           | GatewayIntents.GuildMessages
                           | GatewayIntents.MessageContent
        }));
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<DiscordSocketClient>();
            return new InteractionService(client, new InteractionServiceConfig
            {
                DefaultRunMode = RunMode.Async
            });
        });
        // IDiscordClient: AdminNotifier 주입용 (DiscordSocketClient가 구현)
        services.AddSingleton<IDiscordClient>(sp => sp.GetRequiredService<DiscordSocketClient>());
        services.AddSingleton<IAdminNotifier, AdminNotifier>();
        services.AddSingleton<MessageHandler>();
        services.AddSingleton<SlashCommandHandler>();
        services.AddHostedService<BotClient>();
    })
    .Build();

await host.RunAsync();
