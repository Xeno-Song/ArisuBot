using ArisuBot.Dashboard.Services;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using Microsoft.Extensions.Options;

// Dashboard 진입점. Blazor Server + TCP 이벤트 수신 + MongoDB 쿼리.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Options 바인딩
builder.Services.Configure<MonitorServerOptions>(
    builder.Configuration.GetSection(MonitorServerOptions.SectionName));
builder.Services.Configure<ControlApiOptions>(
    builder.Configuration.GetSection(ControlApiOptions.SectionName));
builder.Services.Configure<MongoDbOptions>(
    builder.Configuration.GetSection(MongoDbOptions.SectionName));

// MongoDB (읽기 전용)
builder.Services.AddSingleton<MongoDbContext>();
builder.Services.AddSingleton<MongoQueryService>();

// Dashboard services
builder.Services.AddSingleton<DashboardStateService>();
builder.Services.AddHostedService<TcpEventReceiver>();

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
