using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Options;
using ArisuBot.LLM.Services;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

/// <summary>GeminiProvider가 비활성화된 tool 호출 시 거부 및 이벤트 emit 검증.</summary>
public class GeminiProviderDisabledToolTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();
    private readonly Mock<ILlmMonitorServer>   _pipeMock   = new();
    private readonly Mock<IErrorLogger>        _errorLoggerMock = new();
    private readonly ToolStateService          _toolStateService = new();
    private static readonly LLMToolExecutionContext ToolContext = new(GuildId: 1UL, ChannelId: 2UL);

    private GeminiProvider CreateSut()
    {
        var geminiOpts = Options.Create(new GeminiOptions { Model = "test-model", ApiKey = "key" });
        var llmOpts    = Options.Create(new LLMOptions { MaxTokens = 100, Temperature = 0.5f, MaxToolIterations = 5 });
        return new GeminiProvider(
            _streamMock.Object, _pipeMock.Object, _errorLoggerMock.Object,
            _toolStateService, geminiOpts, llmOpts,
            new Mock<ILogger<GeminiProvider>>().Object);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ToAsyncEnumerable(
        params GenerateContentResponse[] items)
    {
        foreach (var item in items) yield return item;
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<LLMResponse> source)
    {
        await foreach (var _ in source) { }
    }

    private static GenerateContentResponse MakeFunctionCallChunk(string toolName) => new()
    {
        Candidates =
        [
            new Candidate
            {
                Content = new Content
                {
                    Parts = [new Part { FunctionCall = new FunctionCall { Name = toolName, Args = [] } }]
                }
            }
        ]
    };

    private static GenerateContentResponse MakeTextChunk(string text) => new()
    {
        Candidates = [new Candidate { Content = new Content { Parts = [new Part { Text = text }] } }],
        UsageMetadata = new GenerateContentResponseUsageMetadata()
    };

    [Fact]
    public async Task DisabledTool_EmitsStartedThenFailed_AndDoesNotExecute()
    {
        // tool 비활성화
        _toolStateService.SetEnabled("blocked_tool", false);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(() => ++callCount == 1
                ? ToAsyncEnumerable(MakeFunctionCallChunk("blocked_tool"))
                : ToAsyncEnumerable(MakeTextChunk("done")));

        _streamMock.Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<List<Content>>(),
                It.IsAny<CountTokensConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CountTokensResponse());

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var emittedEvents = new List<LlmMonitorEvent>();
        _pipeMock.Setup(p => p.Emit(It.IsAny<LlmMonitorEvent>()))
            .Callback<LlmMonitorEvent>(e => emittedEvents.Add(e));

        var toolMock = new Mock<ILLMTool>();
        toolMock.Setup(t => t.Name).Returns("blocked_tool");
        toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = "blocked_tool", Description = "test", ParametersJsonSchema = "{}"
        });

        await ConsumeAsync(CreateSut().GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [toolMock.Object], toolContext: ToolContext));

        var started = emittedEvents.OfType<ToolCallStartedEvent>().ToList();
        var failed  = emittedEvents.OfType<ToolCallFailedEvent>().ToList();

        // ToolCallStartedEvent는 비활성 check 전 emit → 1개 존재
        Assert.Single(started);
        Assert.Equal("blocked_tool", started[0].ToolName);

        // ToolCallFailedEvent emit — 비활성 메시지 포함
        Assert.Single(failed);
        Assert.Equal("blocked_tool", failed[0].ToolName);
        Assert.Contains("비활성화", failed[0].ErrorMessage);

        // ExecuteAsync 호출 없어야 함
        toolMock.Verify(t => t.ExecuteAsync(
            It.IsAny<IReadOnlyDictionary<string, object>>(),
            It.IsAny<LLMToolExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // ErrorLogger 호출됨 (fire-and-forget)
        _errorLoggerMock.Verify(e => e.LogAsync(
            It.Is<ErrorLogEntry>(x => x.ErrorType == ErrorType.ToolError),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnabledTool_Executes_NormalFlow()
    {
        // 기본값: 미등록 tool은 활성(true)
        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(() => ++callCount == 1
                ? ToAsyncEnumerable(MakeFunctionCallChunk("active_tool"))
                : ToAsyncEnumerable(MakeTextChunk("done")));

        _streamMock.Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<List<Content>>(),
                It.IsAny<CountTokensConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CountTokensResponse());

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _pipeMock.Setup(p => p.Emit(It.IsAny<LlmMonitorEvent>()));

        var toolMock = new Mock<ILLMTool>();
        toolMock.Setup(t => t.Name).Returns("active_tool");
        toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = "active_tool", Description = "test", ParametersJsonSchema = "{}"
        });
        toolMock.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        await ConsumeAsync(CreateSut().GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [toolMock.Object], toolContext: ToolContext));

        // ExecuteAsync 정상 호출됨
        toolMock.Verify(t => t.ExecuteAsync(
            It.IsAny<IReadOnlyDictionary<string, object>>(),
            It.IsAny<LLMToolExecutionContext>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
