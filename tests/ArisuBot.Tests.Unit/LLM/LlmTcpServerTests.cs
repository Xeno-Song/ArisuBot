using System.Text.Json;
using ArisuBot.LLM.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

/// <summary>LlmTcpServer 단위 테스트 — 클라이언트 없는 환경에서 Emit이 예외 없이 동작하는지 확인.</summary>
public class LlmTcpServerTests
{
    private static LlmTcpServer CreateSut()
        => new(Options.Create(new MonitorServerOptions()), new Mock<ILogger<LlmTcpServer>>().Object);

    [Fact]
    public void Emit_WhenNoClientConnected_DoesNotThrow()
    {
        // TCP 클라이언트 없어도 Emit은 fire-and-forget — 예외 없이 통과해야 함
        var sut = CreateSut();

        var ex = Record.Exception(() =>
            sut.Emit(new TokenUsageEvent("ctx-1", 100, 50, 20, "gemini-pro")));

        Assert.Null(ex);
    }

    [Fact]
    public void Emit_CacheCreatedEvent_DoesNotThrow()
    {
        var sut = CreateSut();

        var ex = Record.Exception(() =>
            sut.Emit(new CacheCreatedEvent("cache-name", 500, "ctx-2", DateTimeOffset.UtcNow.AddHours(1))));

        Assert.Null(ex);
    }

    [Fact]
    public void Emit_CacheDeletedEvent_DoesNotThrow()
    {
        var sut = CreateSut();

        var ex = Record.Exception(() =>
            sut.Emit(new CacheDeletedEvent("cache-name", "cleanup")));

        Assert.Null(ex);
    }

    [Fact]
    public void Emit_ModelStatusEvent_DoesNotThrow()
    {
        var sut = CreateSut();

        var ex = Record.Exception(() =>
            sut.Emit(new ModelStatusEvent("fallback-model", "primary-model")));

        Assert.Null(ex);
    }

    [Fact]
    public void Serialize_TokenUsageEvent_IncludesTypeDiscriminator()
    {
        // 폴리모픽 직렬화 시 "type": "TOKEN_USAGE" 필드가 포함되어야 함
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt = new TokenUsageEvent("ctx", 10, 5, 2, "model-x");

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("\"type\"", json);
        Assert.Contains("TOKEN_USAGE", json);
        Assert.Contains("\"contextId\"", json);
        Assert.Contains("\"tokensIn\"", json);
    }

    [Fact]
    public void Serialize_CacheCreatedEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt = new CacheCreatedEvent("cache-abc", 300, "ctx", DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("\"type\"", json);
        Assert.Contains("CACHE_CREATED", json);
        Assert.Contains("\"cacheName\"", json);
    }

    [Fact]
    public void Serialize_ModelStatusEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt = new ModelStatusEvent("fallback", "primary");

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("\"type\"", json);
        Assert.Contains("MODEL_STATUS", json);
        Assert.Contains("\"currentModel\"", json);
        Assert.Contains("\"previousModel\"", json);
    }

    // --- 신규 이벤트 직렬화 ---

    [Fact]
    public void Serialize_ProcessingStartedEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt  = new ProcessingStartedEvent("ctx-1", 3);

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("PROCESSING_STARTED", json);
        Assert.Contains("\"contextId\"", json);
        Assert.Contains("\"messageCount\"", json);
    }

    [Fact]
    public void Serialize_ProcessingCompletedEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt  = new ProcessingCompletedEvent("ctx-1", 250L);

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("PROCESSING_COMPLETED", json);
        Assert.Contains("\"durationMs\"", json);
    }

    [Fact]
    public void Serialize_ToolCallStartedEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt  = new ToolCallStartedEvent("ctx-1", "discord_timeout_user", "{\"user\":\"xeno\"}");

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("TOOL_CALL_STARTED", json);
        Assert.Contains("\"toolName\"", json);
        Assert.Contains("\"argumentsJson\"", json);
    }

    [Fact]
    public void Serialize_ToolCallCompletedEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt  = new ToolCallCompletedEvent("ctx-1", "discord_timeout_user", 120L);

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("TOOL_CALL_COMPLETED", json);
        Assert.Contains("\"toolName\"", json);
        Assert.Contains("\"durationMs\"", json);
    }

    [Fact]
    public void Serialize_ToolCallFailedEvent_IncludesTypeDiscriminator()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var evt  = new ToolCallFailedEvent("ctx-1", "discord_timeout_user", "permission denied", 30L);

        var json = JsonSerializer.Serialize<LlmMonitorEvent>(evt, opts);

        Assert.Contains("TOOL_CALL_FAILED", json);
        Assert.Contains("\"toolName\"", json);
        Assert.Contains("\"errorMessage\"", json);
    }

    [Fact]
    public void EmitProcessingStarted_DoesNotThrow()
    {
        var sut = CreateSut();
        var ex  = Record.Exception(() => sut.EmitProcessingStarted("ctx-1", 2));
        Assert.Null(ex);
    }

    [Fact]
    public void EmitProcessingCompleted_DoesNotThrow()
    {
        var sut = CreateSut();
        var ex  = Record.Exception(() => sut.EmitProcessingCompleted("ctx-1", 300L));
        Assert.Null(ex);
    }
}
