using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using ArisuBot.Discord;
using ArisuBot.Discord.Handlers;
using ArisuBot.Discord.Options;
using ArisuBot.Discord.Services;
using ArisuBot.Discord.Tools;
using ArisuBot.Host.BackgroundServices;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using ArisuBot.Infrastructure.Prompts;
using ArisuBot.LLM.Extensions;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

// Bot 진입점. DI 구성, HTTP Control API 서버(port 9877), 호스트 시작.
// ContentRootPath: 실행 파일 위치 지정 — appsettings.json이 복사된 bin/Debug/net8.0/ 탐색
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args            = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    // appsettings.Local.json: 시크릿 설정. 존재하지 않으면 건너뜀.
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables();

builder.Logging
    .ClearProviders()
    .AddLog4Net("log4net.config");

// Dashboard HTTP Control API — port 9877, localhost only
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(9877));

var config = builder.Configuration;
var services = builder.Services;

// Options 바인딩
services.Configure<DiscordOptions>(config.GetSection(DiscordOptions.SectionName));
services.Configure<DiscordToolOptions>(config.GetSection(DiscordToolOptions.SectionName));
services.Configure<MongoDbOptions>(config.GetSection(MongoDbOptions.SectionName));
services.Configure<MemoryOptions>(config.GetSection(MemoryOptions.SectionName));
services.Configure<CompactionOptions>(config.GetSection(CompactionOptions.SectionName));
services.Configure<SemanticMemoryOptions>(config.GetSection(SemanticMemoryOptions.SectionName));

// Infrastructure — MongoDB
services.AddSingleton<MongoDbContext>();
services.AddSingleton<IConversationRepository, ConversationRepository>();
services.AddSingleton<ISemanticMemoryRepository, SemanticMemoryRepository>();
services.AddSingleton<IAIMessageLogger, AIMessageLogger>();
services.AddSingleton<IErrorLogger, ErrorLogger>();

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
services.AddSingleton<CompactionTriggerEvaluator>();
services.AddSingleton<ICompactionService, CompactionService>();
services.AddSingleton<ISemanticMemoryService, SemanticMemoryService>();

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
services.AddHostedService<CompactionBackgroundService>();

var app = builder.Build();

// ─── Dashboard HTTP Control API ────────────────────────────────────────────

/// <summary>tool 이름 → 활성화 상태 전체 조회.</summary>
app.MapGet("/api/tools", (IToolStateService toolState) =>
    Results.Ok(toolState.GetAll()));

/// <summary>PUT /api/tools/{name} body: {"enabled":true/false} — tool 활성화 상태 변경.</summary>
app.MapPut("/api/tools/{name}", (string name, ToolStateRequest req, IToolStateService toolState) =>
{
    toolState.SetEnabled(name, req.Enabled);
    return Results.Ok(new { name, req.Enabled });
});

/// <summary>현재 활성 세션 목록 요약 조회. 각 세션의 id, type, targetId, messageCount, createdAt, updatedAt 반환.</summary>
app.MapGet("/api/sessions", async (IConversationRepository repo) =>
{
    var sessions = await repo.GetAllActiveContextsAsync();
    var summaries = sessions.Select(s => new
    {
        s.Id,
        Type       = s.Type.ToString(),
        s.TargetId,
        MessageCount = s.Messages.Count,
        s.CreatedAt,
        s.UpdatedAt
    });
    return Results.Ok(summaries);
});

/// <summary>특정 세션의 총 토큰 사용량 집계 반환.</summary>
app.MapGet("/api/sessions/{id}/tokens", async (string id, IConversationRepository repo) =>
{
    var ctx = await repo.GetContextByIdAsync(id);
    if (ctx is null) return Results.NotFound();

    var total = new
    {
        TokensIn        = ctx.TokenUsage.Sum(t => t.TokensIn),
        TokensOut       = ctx.TokenUsage.Sum(t => t.TokensOut),
        TokensCachedIn  = ctx.TokenUsage.Sum(t => t.TokensCachedIn),
        RecordCount     = ctx.TokenUsage.Count
    };
    return Results.Ok(total);
});

// ───────────────────────────────────────────────────────────────────────────

await app.RunAsync();

/// <summary>PUT /api/tools/{name} 요청 바디.</summary>
record ToolStateRequest(bool Enabled);
