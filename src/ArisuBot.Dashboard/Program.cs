using ArisuBot.Dashboard.Services;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using Microsoft.Extensions.Options;

// Dashboard 진입점. Blazor Server + TCP 이벤트 수신 + MongoDB 쿼리.
var builder = WebApplication.CreateBuilder(args);

// 로컬 전용 설정 오버라이드 (gitignore 대상, 커밋 금지)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Options 바인딩
builder.Services.Configure<MonitorServerOptions>(
    builder.Configuration.GetSection(MonitorServerOptions.SectionName));
builder.Services.Configure<ControlApiOptions>(
    builder.Configuration.GetSection(ControlApiOptions.SectionName));
builder.Services.Configure<MongoDbOptions>(
    builder.Configuration.GetSection(MongoDbOptions.SectionName));
builder.Services.Configure<TokenSyncOptions>(
    builder.Configuration.GetSection(TokenSyncOptions.SectionName));

// MongoDB (읽기 전용)
builder.Services.AddSingleton<MongoDbContext>();
// IMongoQueryService: 인터페이스로 등록해 페이지·서비스·테스트 모두에서 주입 가능
builder.Services.AddSingleton<IMongoQueryService, MongoQueryService>();
builder.Services.AddSingleton<MongoQueryService>(sp => (MongoQueryService)sp.GetRequiredService<IMongoQueryService>());

// Dashboard services
builder.Services.AddSingleton<DashboardStateService>();
builder.Services.AddHostedService<TcpEventReceiver>();
// TokenSyncService: singleton 등록 후 HostedService로도 등록 (UI에서 SyncNowAsync 직접 호출 가능)
builder.Services.AddSingleton<TokenSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TokenSyncService>());

// HTTP Control API 클라이언트
builder.Services.AddHttpClient<ControlApiClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<ControlApiOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await app.RunAsync();
