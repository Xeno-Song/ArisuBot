using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Options;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

public class GeminiProviderRetryTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();

    /// <summary>지정한 설정으로 GeminiProvider 인스턴스 생성.</summary>
    private GeminiProvider CreateSut(
        string? fallbackModel = "fallback-model",
        int streamRetryCount = 2,
        int retryDelayMs = 0) // 단위 테스트에서는 딜레이 없음
    {
        var geminiOpts = Options.Create(new GeminiOptions
        {
            Model            = "primary-model",
            ApiKey           = "key",
            FallbackModel    = fallbackModel,
            StreamRetryCount = streamRetryCount,
            RetryDelayMs     = retryDelayMs
        });
        var llmOpts = Options.Create(new LLMOptions
        {
            MaxTokens        = 100,
            Temperature      = 0.5f,
            MaxToolIterations = 5
        });
        return new GeminiProvider(
            _streamMock.Object, geminiOpts, llmOpts,
            new Mock<ILogger<GeminiProvider>>().Object);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ToAsyncEnumerable(
        params GenerateContentResponse[] items)
    {
        foreach (var item in items)
            yield return item;
    }

    /// <summary>스트리밍 도중 예외를 throw하는 IAsyncEnumerable.</summary>
    private static async IAsyncEnumerable<GenerateContentResponse> ThrowDuringStream(Exception ex)
    {
        await Task.Yield(); // 실제 enumeration 중 throw 보장
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task<List<LLMResponse>> CollectAsync(IAsyncEnumerable<LLMResponse> source)
    {
        var list = new List<LLMResponse>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    private static GenerateContentResponse MakeTextChunk(string text) => new()
    {
        Candidates =
        [
            new Candidate { Content = new Content { Parts = [new Part { Text = text }] } }
        ]
    };

    private static GenerateContentResponse MakeFunctionCallChunk(string toolName) => new()
    {
        Candidates =
        [
            new Candidate
            {
                Content = new Content
                {
                    Parts = [new Part { FunctionCall = new FunctionCall { Name = toolName, Args = new Dictionary<string, object>() } }]
                }
            }
        ]
    };

    // ─── ServerError 재시도 ────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_ServerError_RetriesUpToCount_ThenSwitchesToFallback()
    {
        // StreamRetryCount = 2 → primary 3회(1 원본 + 2 재시도) 후 fallback 전환 → 성공
        var sut = CreateSut(fallbackModel: "fallback-model", streamRetryCount: 2);

        var serverError = new ServerError("high demand");
        var primaryCallCount = 0;
        var fallbackCallCount = 0;

        _streamMock.Setup(s => s.StreamAsync(
                "primary-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                primaryCallCount++;
                return ThrowDuringStream(serverError);
            });

        _streamMock.Setup(s => s.StreamAsync(
                "fallback-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                fallbackCallCount++;
                return ToAsyncEnumerable(MakeTextChunk("fallback response"));
            });

        var result = await CollectAsync(sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        // primary 3회 호출(1 + 2 retries), fallback 1회
        Assert.Equal(3, primaryCallCount);
        Assert.Equal(1, fallbackCallCount);
        Assert.Single(result);
        Assert.Equal("fallback response", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_ServerError_RetriesSucceedBeforeExhausted()
    {
        // 첫 번째 호출 실패 → 재시도에서 성공 (fallback 전환 없음)
        var sut = CreateSut(streamRetryCount: 2);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                "primary-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ThrowDuringStream(new ServerError("transient"))
                    : ToAsyncEnumerable(MakeTextChunk("recovered"));
            });

        var result = await CollectAsync(sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Equal(2, callCount);
        Assert.Equal("recovered", result[0].Content);
        // fallback 모델은 한 번도 호출되지 않아야 함
        _streamMock.Verify(s => s.StreamAsync(
            "fallback-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_ServerError_NoFallbackConfigured_Throws()
    {
        // FallbackModel = null → 재시도 소진 후 ServerError propagate
        var sut = CreateSut(fallbackModel: null, streamRetryCount: 1);

        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() => ThrowDuringStream(new ServerError("overloaded")));

        await Assert.ThrowsAsync<ServerError>(() =>
            CollectAsync(sut.GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "hi" }])));
    }

    [Fact]
    public async Task GenerateAsync_FallbackStickyAcrossToolIterations()
    {
        // iteration 0: primary → ServerError → fallback 전환 → FunctionCall 반환
        // iteration 1: fallback 모델 사용 → 텍스트 반환 (primary 재호출 없음)
        var sut = CreateSut(fallbackModel: "fallback-model", streamRetryCount: 0);

        var toolMock = new Mock<ILLMTool>();
        toolMock.Setup(t => t.Name).Returns("test_tool");
        toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition
            { Name = "test_tool", Description = "d", ParametersJsonSchema = "{}" });
        toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        var primaryCallCount = 0;
        var fallbackCallCount = 0;

        _streamMock.Setup(s => s.StreamAsync(
                "primary-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                primaryCallCount++;
                return ThrowDuringStream(new ServerError("overloaded"));
            });

        _streamMock.Setup(s => s.StreamAsync(
                "fallback-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                fallbackCallCount++;
                return fallbackCallCount == 1
                    ? ToAsyncEnumerable(MakeFunctionCallChunk("test_tool"))
                    : ToAsyncEnumerable(MakeTextChunk("sticky fallback response"));
            });

        var toolContext = new LLMToolExecutionContext(GuildId: 111UL, ChannelId: 222UL);
        var result = await CollectAsync(sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [toolMock.Object], toolContext: toolContext));

        // primary 1회 → fallback 2회 (iteration 0 + iteration 1)
        Assert.Equal(1, primaryCallCount);
        Assert.Equal(2, fallbackCallCount);
        Assert.Equal("sticky fallback response", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_FallbackModelAlsoFails_Throws()
    {
        // primary + fallback 모두 ServerError → 최종 throw
        // StreamRetryCount = 0: primary 즉시 → fallback 전환 → fallback 재시도 0회 → throw
        var sut = CreateSut(fallbackModel: "fallback-model", streamRetryCount: 0);

        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() => ThrowDuringStream(new ServerError("all models failed")));

        await Assert.ThrowsAsync<ServerError>(() =>
            CollectAsync(sut.GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "hi" }])));
    }

    // ─── ClientError 즉시 실패 ───────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_ClientError_PropagatesImmediatelyWithoutRetry()
    {
        // ClientError → 재시도 없이 즉시 throw
        var sut = CreateSut(streamRetryCount: 2);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return ThrowDuringStream(new ClientError("client error"));
            });

        await Assert.ThrowsAsync<ClientError>(() =>
            CollectAsync(sut.GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "hi" }])));

        // 1회만 호출 — 재시도 없음
        Assert.Equal(1, callCount);
    }

    // ─── OperationCanceledException + RetryDelay ─────────────────────────

    [Fact]
    public async Task GenerateAsync_OperationCancelledDuringStream_RethrowsImmediately()
    {
        // 스트리밍 중 OperationCanceledException — 재시도 없이 즉시 propagate
        var sut = CreateSut(streamRetryCount: 2);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return ThrowDuringStream(new OperationCanceledException());
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CollectAsync(sut.GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "hi" }])));

        // 재시도 없이 1회만 호출
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GenerateAsync_ServerError_WithPositiveRetryDelay_RetriesAndSucceeds()
    {
        // RetryDelayMs > 0 경로 커버 — 실제 딜레이(1ms)가 있어도 정상 동작 확인
        var sut = CreateSut(streamRetryCount: 1, retryDelayMs: 1);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                "primary-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ThrowDuringStream(new ServerError("transient"))
                    : ToAsyncEnumerable(MakeTextChunk("recovered after delay"));
            });

        var result = await CollectAsync(sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Equal(2, callCount);
        Assert.Equal("recovered after delay", result[0].Content);
    }

    // ─── Fallback 전환 시 Cache 초기화 ──────────────────────────────────

    [Fact]
    public async Task GenerateAsync_FallbackTransition_ClearsConfigCachedContent()
    {
        // fallback 전환 후 StreamAsync는 CachedContent == null인 config로 호출되어야 함
        var sut = CreateSut(fallbackModel: "fallback-model", streamRetryCount: 0);

        _streamMock.Setup(s => s.StreamAsync(
                "primary-model", It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() => ThrowDuringStream(new ServerError("overloaded")));

        _streamMock.Setup(s => s.StreamAsync(
                "fallback-model", It.IsAny<IEnumerable<Content>>(),
                It.Is<GenerateContentConfig>(c => c.CachedContent == null)))
            .Returns(ToAsyncEnumerable(MakeTextChunk("ok")));

        // CacheHint 포함 요청 — fallback 전환 시 cache 제거 확인
        var cacheHint = new CacheHint("cached-content-name", 2);
        var result = await CollectAsync(sut.GenerateAsync(
            [
                new ChatMessage { Role = Role.System, Content = "sys" },
                new ChatMessage { Role = Role.User, Content = "hi" },
                new ChatMessage { Role = Role.User, Content = "msg1" },
                new ChatMessage { Role = Role.User, Content = "msg2" }
            ],
            cacheHint: cacheHint));

        // fallback 모델에 CachedContent=null로 호출 → Verify
        _streamMock.Verify(s => s.StreamAsync(
            "fallback-model", It.IsAny<IEnumerable<Content>>(),
            It.Is<GenerateContentConfig>(c => c.CachedContent == null)),
            Times.Once);

        Assert.Equal("ok", result[0].Content);
    }
}
