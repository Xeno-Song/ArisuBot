using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ArisuBot.LLM.Monitoring;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Dashboard.Services;

/// <summary>
/// Host TCP 서버(port 9876)에 연결해 LlmMonitorEvent를 수신하고 DashboardStateService에 전달.
/// IHostedService로 자동 시작. 연결 끊김 시 1초 후 재연결.
/// </summary>
public sealed class TcpEventReceiver : IHostedService, IDisposable
{
    private readonly MonitorServerOptions _options;
    private readonly DashboardStateService _state;
    private readonly ILogger<TcpEventReceiver> _logger;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CancellationTokenSource? _cts;
    private Task? _task;

    public TcpEventReceiver(
        IOptions<MonitorServerOptions> options,
        DashboardStateService state,
        ILogger<TcpEventReceiver> logger)
    {
        _options = options.Value;
        _state   = state;
        _logger  = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_task is not null)
            await _task.ConfigureAwait(false);
    }

    public void Dispose() => _cts?.Dispose();

    /// <summary>연결 → 수신 → 재연결 루프.</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ConnectAndReadAsync(ct);
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>TCP 연결 후 이벤트 수신. 연결 실패 또는 종료 시 반환.</summary>
    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        using var client = new TcpClient();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(_options.Host, _options.Port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return; // 5초 타임아웃
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SocketException)
        {
            return; // Host 미실행
        }

        _state.SetConnected(true);
        _logger.LogInformation("TCP 연결 성공 — {Host}:{Port}", _options.Host, _options.Port);

        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ProcessLine(line);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { return; }
        finally
        {
            _state.SetConnected(false);
            _logger.LogInformation("TCP 연결 종료");
        }
    }

    /// <summary>JSON 줄을 LlmMonitorEvent로 역직렬화 후 상태에 반영.</summary>
    private void ProcessLine(string json)
    {
        LlmMonitorEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<LlmMonitorEvent>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "이벤트 역직렬화 실패 — json={Json}", json);
            return;
        }

        if (evt is null) return;
        _state.Apply(evt);
    }
}
