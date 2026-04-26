using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Options;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

/// <summary>GeminiProvider가 tool 실행 시 ILlmMonitorServer에 올바른 이벤트를 emit하는지 검증.</summary>
public class GeminiProviderToolEventTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();
    private readonly Mock<ILlmMonitorServer> _pipeMock = new();
    private readonly Mock<IErrorLogger> _errorLoggerMock = new();
    private static readonly LLMToolExecutionContext ToolContext = new(GuildId: 1UL, ChannelId: 2UL);

    private GeminiProvider CreateSut()
    {
        var geminiOpts = Options.Create(new GeminiOptions { Model = "test-model", ApiKey = "key" });
        var llmOpts    = Options.Create(new LLMOptions { MaxTokens = 100, Temperature = 0.5f, MaxToolIterations = 5 });
        return new GeminiProvider(
            _streamMock.Object, _pipeMock.Object, _errorLoggerMock.Object,
            geminiOpts, llmOpts, new Mock<ILogger<GeminiProvider>>().Object);
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

    private Mock<ILLMTool> CreateTool(string name, bool shouldSucceed = true)
    {
        var tool = new Mock<ILLMTool>();
        tool.Setup(t => t.Name).Returns(name);
        tool.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = name, Description = "test", ParametersJsonSchema = "{}"
        });

        if (shouldSucceed)
            tool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object>>(),
                    It.IsAny<LLMToolExecutionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ToolResult { Success = true });
        else
            tool.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object>>(),
                    It.IsAny<LLMToolExecutionContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("tool failed"));

        return tool;
    }

    [Fact]
    public async Task ToolExecution_EmitsStartedThenCompleted_OnSuccess()
    {
        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(() => ++callCount == 1
                ? ToAsyncEnumerable(MakeFunctionCallChunk("my_tool"))
                : ToAsyncEnumerable(MakeTextChunk("done")));

        _streamMock.Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<List<Content>>(),
                It.IsAny<CountTokensConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CountTokensResponse());

        var emittedEvents = new List<LlmMonitorEvent>();
        _pipeMock.Setup(p => p.Emit(It.IsAny<LlmMonitorEvent>()))
            .Callback<LlmMonitorEvent>(e => emittedEvents.Add(e));

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = CreateTool("my_tool", shouldSucceed: true);
        await ConsumeAsync(CreateSut().GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [tool.Object], toolContext: ToolContext));

        var started   = emittedEvents.OfType<ToolCallStartedEvent>().ToList();
        var completed = emittedEvents.OfType<ToolCallCompletedEvent>().ToList();
        var failed    = emittedEvents.OfType<ToolCallFailedEvent>().ToList();

        Assert.Single(started);
        Assert.Equal("my_tool", started[0].ToolName);
        Assert.Single(completed);
        Assert.Equal("my_tool", completed[0].ToolName);
        Assert.Empty(failed);
    }

    [Fact]
    public async Task ToolExecution_EmitsStartedThenFailed_OnException()
    {
        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(() => ++callCount == 1
                ? ToAsyncEnumerable(MakeFunctionCallChunk("bad_tool"))
                : ToAsyncEnumerable(MakeTextChunk("done")));

        _streamMock.Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<List<Content>>(),
                It.IsAny<CountTokensConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CountTokensResponse());

        var emittedEvents = new List<LlmMonitorEvent>();
        _pipeMock.Setup(p => p.Emit(It.IsAny<LlmMonitorEvent>()))
            .Callback<LlmMonitorEvent>(e => emittedEvents.Add(e));

        _errorLoggerMock.Setup(e => e.LogAsync(It.IsAny<ErrorLogEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = CreateTool("bad_tool", shouldSucceed: false);
        await ConsumeAsync(CreateSut().GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [tool.Object], toolContext: ToolContext));

        var started = emittedEvents.OfType<ToolCallStartedEvent>().ToList();
        var failed  = emittedEvents.OfType<ToolCallFailedEvent>().ToList();

        Assert.Single(started);
        Assert.Equal("bad_tool", started[0].ToolName);
        Assert.Single(failed);
        Assert.Equal("bad_tool", failed[0].ToolName);
        Assert.Contains("tool failed", failed[0].ErrorMessage);
    }

    [Fact]
    public async Task UnknownTool_EmitsStartedThenFailed()
    {
        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<GenerateContentConfig>()))
            .Returns(() => ++callCount == 1
                ? ToAsyncEnumerable(MakeFunctionCallChunk("unknown_tool"))
                : ToAsyncEnumerable(MakeTextChunk("done")));

        _streamMock.Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<List<Content>>(),
                It.IsAny<CountTokensConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CountTokensResponse());

        var emittedEvents = new List<LlmMonitorEvent>();
        _pipeMock.Setup(p => p.Emit(It.IsAny<LlmMonitorEvent>()))
            .Callback<LlmMonitorEvent>(e => emittedEvents.Add(e));

        // tools 목록에 unknown_tool 없음
        var knownTool = CreateTool("known_tool");
        await ConsumeAsync(CreateSut().GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [knownTool.Object], toolContext: ToolContext));

        var failed = emittedEvents.OfType<ToolCallFailedEvent>().ToList();

        Assert.Single(failed);
        Assert.Equal("unknown_tool", failed[0].ToolName);
    }
}
