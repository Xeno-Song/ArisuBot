using ArisuBot.LLM.Monitoring;
using ArisuBot.Monitor;
using Microsoft.Extensions.Configuration;

// 설정 로드 — appsettings.json에서 MonitorServer 섹션 읽기
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var options = config.GetSection(MonitorServerOptions.SectionName).Get<MonitorServerOptions>()
              ?? new MonitorServerOptions();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var app = new LlmMonitorApp(options);
await app.RunAsync(cts.Token);
