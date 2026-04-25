using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using ArisuBot.Discord;
using ArisuBot.Discord.Handlers;
using ArisuBot.Discord.Options;
using ArisuBot.Discord.Services;
using ArisuBot.Discord.Tools;
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
        services.Configure<DiscordToolOptions>(config.GetSection(DiscordToolOptions.SectionName));
        services.Configure<MongoDbOptions>(config.GetSection(MongoDbOptions.SectionName));
        services.Configure<MemoryOptions>(config.GetSection(MemoryOptions.SectionName));
        services.Configure<CompactionOptions>(config.GetSection(CompactionOptions.SectionName));

        // Infrastructure — MongoDB
        services.AddSingleton<MongoDbContext>();
        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IAIMessageLogger, AIMessageLogger>();

        // Infrastructure — Prompts (봇 시작 시 파일 로드, /new-session으로 재로드)
        // 개발 환경: bin/Debug/net8.0/에서 ../../../ 로 올라가면 프로젝트 소스 디렉터리.
        // 소스 prompts/가 존재하면 직접 참조해 재빌드 없이 파일 수정 즉시 반영.
        // 배포 환경(publish/): ../../../ 경로에 prompts/ 없으므로 output 디렉터리로 fallback.
        var sourcePromptsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../prompts"));
        var promptsPath = Directory.Exists(sourcePromptsPath)
            ? sourcePromptsPath
            : Path.Combine(AppContext.BaseDirectory, "prompts");
        services.AddSingleton<IPromptLoader>(_ => new FilePromptLoader(promptsPath));

        // Core
        services.AddSingleton<ConversationService>();

        // LLM
        services.AddLLMProvider(config);

        // Discord — MessageContent, GuildMembers: 개발자 포털에서 Privileged Intents 수동 활성화 필요
        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                           | GatewayIntents.GuildMessages
                           | GatewayIntents.MessageContent
                           | GatewayIntents.GuildMembers   // ListChannelUsersTool에서 서버 전체 멤버 조회 필요
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
        // LLM 툴 등록 — IEnumerable<ILLMTool>로 MessageHandler에 주입됨
        services.AddSingleton<ILLMTool, TimeoutUserTool>();
        services.AddSingleton<ILLMTool, ListChannelUsersTool>();
        services.AddSingleton<MessageHandler>();
        services.AddSingleton<SlashCommandHandler>();
        services.AddHostedService<BotClient>();
    })
    .Build();

await host.RunAsync();
