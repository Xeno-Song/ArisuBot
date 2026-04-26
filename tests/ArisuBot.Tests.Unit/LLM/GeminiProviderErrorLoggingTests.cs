using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Options;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

/// <summary>GeminiProvider가 에러 발생 시 IErrorLogger.LogAsync를 호출하는지 검증.</summary>
public class GeminiProviderErrorLoggingTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();
    private readonly Mock<ILlmMonitorServer> _pipeMock = new();
    private readonly Mock<IErrorLogger> _errorLoggerMock = new();

    private GeminiProvider CreateSut(string? fallbackModel = null, int streamRetryCount = 0)
    {
        var geminiOpts = Options.Create(new GeminiOptions
        {
            Model            = "primary-model",
            ApiKey           = "key",
            FallbackModel    = fallbackModel,
            StreamRetryCount = streamRetryCount,
            RetryDelayMs     = 0
        });
        var llmOpts = Options.Create(new LLMOptions
        {
            MaxTokens         = 100,
            Temperature       = 0.5f,
            MaxToolIterations = 5
        });
        return new GeminiProvider(
            _streamMock.Object, _pipeMock.Object, _errorLoggerMock.Object,
            geminiOpts, llmOpts, new Mock<ILogger<GeminiProvider>>().Object);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ThrowDuringStream(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<LLMResponse> source)
    {
        await foreach (var _ in source) { }
    }

    [Fact]
    public async Task GenerateAsync_ClientError_LogsLLMError()
    {
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(ThrowDuringStream(new ClientError("bad request")));

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<ClientError>(() =>
            ConsumeAsync(CreateSut().GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "hi" }])));

        _errorLoggerMock.Verify(e => e.LogAsync(
            It.Is<ErrorLogEntry>(en => en.ErrorType == ErrorType.LLMError),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_ServerErrorWithNoFallback_LogsLLMError()
    {
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(ThrowDuringStream(new ServerError("server error")));

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<ServerError>(() =>
            ConsumeAsync(CreateSut(fallbackModel: null, streamRetryCount: 0).GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "hi" }])));

        _errorLoggerMock.Verify(e => e.LogAsync(
            It.Is<ErrorLogEntry>(en => en.ErrorType == ErrorType.LLMError),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_ToolExecutionException_LogsToolError()
    {
        // 첫 응답은 FunctionCall → 두 번째 응답은 최종 텍스트
        var toolCallChunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "test_tool", Args = [] } }]
                    }
                }
            ]
        };
        var finalChunk = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = new Content { Parts = [new Part { Text = "done" }] } }],
            UsageMetadata = new GenerateContentResponseUsageMetadata()
        };

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(toolCallChunk)
                    : ToAsyncEnumerable(finalChunk);
            });

        var failingTool = new Mock<ILLMTool>();
        failingTool.Setup(t => t.Name).Returns("test_tool");
        failingTool.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = "test_tool", Description = "fails", ParametersJsonSchema = "{}"
        });
        failingTool.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tool crashed"));

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _streamMock.Setup(s => s.CountTokensAsync(
                It.IsAny<string>(), It.IsAny<List<Content>>(),
                It.IsAny<CountTokensConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CountTokensResponse());

        var sut = CreateSut();
        await ConsumeAsync(sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "use tool" }],
            tools: [failingTool.Object],
            toolContext: new LLMToolExecutionContext(GuildId: 1UL, ChannelId: 2UL)));

        _errorLoggerMock.Verify(e => e.LogAsync(
            It.Is<ErrorLogEntry>(en => en.ErrorType == ErrorType.ToolError),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ToAsyncEnumerable(
        params GenerateContentResponse[] items)
    {
        foreach (var item in items) yield return item;
    }
}
