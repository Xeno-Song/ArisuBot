using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ArisuBot.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.LLM.Monitoring;

/// <summary>
/// TCP 서버로 LLM Monitor / Dashboard Sidecar에 이벤트를 스트리밍 전송한다.
/// IHostedService로 TCP 수신 루프를 관리하며, Sidecar 미연결 시 이벤트는 drop된다.
/// 클라이언트 연결 끊김 시 새 연결 대기로 자동 복귀한다.
/// IProcessingEventEmitter: MessageHandler의 처리 시작/완료 이벤트를 수신해 TCP 스트림으로 전달.
/// </summary>
public sealed class LlmTcpServer : ILlmMonitorServer, IProcessingEventEmitter, IHostedService
{
    private readonly MonitorServerOptions _options;
    private readonly ILogger<LlmTcpServer> _logger;

    // 이벤트 직렬화 후 큐잉 — 초과 시 가장 오래된 항목 drop (모니터링 경로이므로 허용)
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(200)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    public LlmTcpServer(IOptions<MonitorServerOptions> options, ILogger<LlmTcpServer> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    /// <inheritdoc/>
    public void Emit(LlmMonitorEvent evt)
    {
        // LlmMonitorEvent 기반 타입으로 직렬화 → [JsonPolymorphic] 적용되어 "type" discriminator 포함
        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, _jsonOptions);
        // TryWrite: 채널 용량 초과 시 false(drop). 채널 완료 후에도 false. 예외 없음.
        _channel.Writer.TryWrite(json);
    }

    /// <inheritdoc/>
    public void EmitProcessingStarted(string contextId, int messageCount)
        => Emit(new ProcessingStartedEvent(ContextId: contextId, MessageCount: messageCount));

    /// <inheritdoc/>
    public void EmitProcessingCompleted(string contextId, long durationMs)
        => Emit(new ProcessingCompletedEvent(ContextId: contextId, DurationMs: durationMs));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts            = new CancellationTokenSource();
        _backgroundTask = Task.Run(() => RunTcpLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _cts?.Cancel();
        if (_backgroundTask is not null)
            await _backgroundTask.ConfigureAwait(false);
    }

    /// <summary>TCP 리스너 루프. 클라이언트 연결 → 이벤트 전달 → 연결 종료 시 재대기.</summary>
    private async Task RunTcpLoopAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(_options.Host), _options.Port);
        listener.Start();
        _logger.LogInformation("LLM Monitor TCP 서버 시작 — {Host}:{Port}", _options.Host, _options.Port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 단일 클라이언트 지원 — AcceptTcpClientAsync는 취소 토큰 전달 가능(.NET 6+)
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    _logger.LogInformation("LLM Monitor 클라이언트 연결됨");
                    await ServeClientAsync(client.GetStream(), ct);
                    _logger.LogInformation("LLM Monitor 클라이언트 연결 종료");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TCP 루프 오류 — 재시도");
                }
            }
        }
        finally
        {
            listener.Stop();
            _logger.LogInformation("LLM Monitor TCP 서버 종료");
        }
    }

    /// <summary>
    /// 연결된 클라이언트에 채널 이벤트를 스트리밍한다.
    /// 클라이언트 연결 종료(IOException) 또는 셧다운(OperationCanceledException) 시 반환.
    /// 연결 직후 채널에 누적된 구 이벤트를 drain해 신선한 이벤트만 전달한다.
    /// </summary>
    private async Task ServeClientAsync(NetworkStream stream, CancellationToken ct)
    {
        // 구 이벤트 drain — 새 클라이언트는 연결 이후 발생한 이벤트만 수신
        while (_channel.Reader.TryRead(out _)) { }

        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync(ct))
            {
                await writer.WriteLineAsync(line);
            }
        }
        catch (IOException)
        {
            // 클라이언트 연결 종료 — 정상 상황, RunTcpLoopAsync에서 재대기
        }
        catch (OperationCanceledException)
        {
            // 셧다운 요청 — 정상 종료
        }
    }
}
