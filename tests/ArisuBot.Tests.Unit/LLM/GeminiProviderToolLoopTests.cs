using System.Text.Json;
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

public class GeminiProviderToolLoopTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();
    private readonly Mock<ILlmMonitorServer> _pipeMock = new();
    private readonly Mock<ILLMTool> _toolMock = new();
    private readonly GeminiProvider _sut;
    private static readonly LLMToolExecutionContext Context = new(GuildId: 111UL, ChannelId: 222UL);

    public GeminiProviderToolLoopTests()
    {
        var geminiOpts = Options.Create(new GeminiOptions { Model = "test-model", ApiKey = "key" });
        var llmOpts = Options.Create(new LLMOptions { MaxTokens = 100, Temperature = 0.5f, MaxToolIterations = 5 });
        _sut = new GeminiProvider(_streamMock.Object, _pipeMock.Object, geminiOpts, llmOpts,
            new Mock<ILogger<GeminiProvider>>().Object);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ToAsyncEnumerable(
        params GenerateContentResponse[] items)
    {
        foreach (var item in items)
            yield return item;
    }

    /// <summary>IAsyncEnumerable&lt;LLMResponse&gt;를 List로 수집한다. 테스트 헬퍼.</summary>
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

    /// <summary>FunctionCall 파트를 포함하는 청크 생성.</summary>
    private static GenerateContentResponse MakeFunctionCallChunk(string toolName, Dictionary<string, object> args) => new()
    {
        Candidates =
        [
            new Candidate
            {
                Content = new Content
                {
                    Parts = [new Part { FunctionCall = new FunctionCall { Name = toolName, Args = args } }]
                }
            }
        ]
    };

    [Fact]
    public async Task GenerateAsync_WithNullTools_PassesNoToolDeclarationsToProvider()
    {
        // tools=null이면 tool 선언 없이 StreamAsync 호출해야 함
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.Is<GenerateContentConfig>(c => c.Tools == null || c.Tools.Count == 0)))
            .Returns(ToAsyncEnumerable(MakeTextChunk("response")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: null, toolContext: null));

        Assert.Single(result);
        Assert.Equal("response", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_WithToolsButNullContext_PassesNoToolDeclarations()
    {
        // toolContext=null이면 tools가 있어도 선언 안 함
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.Is<GenerateContentConfig>(c => c.Tools == null || c.Tools.Count == 0)))
            .Returns(ToAsyncEnumerable(MakeTextChunk("response")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [_toolMock.Object], toolContext: null));

        Assert.Single(result);
        Assert.Equal("response", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_WithToolsAndContext_PassesToolDeclarationsToStream()
    {
        // tools+context 모두 있으면 StreamAsync에 Tools가 포함된 config 전달
        _toolMock.Setup(t => t.Name).Returns("test_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = "test_tool",
            Description = "test",
            ParametersJsonSchema = "{}"
        });

        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(),
                It.Is<GenerateContentConfig>(c => c.Tools != null && c.Tools.Count > 0)))
            .Returns(ToAsyncEnumerable(MakeTextChunk("response")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [_toolMock.Object], toolContext: Context));

        Assert.Single(result);
        Assert.Equal("response", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_FunctionCallInResponse_ExecutesToolAndCallsStreamAgain()
    {
        // 1회차: FunctionCall 반환 → 툴 실행 → 2회차: 텍스트 반환
        _toolMock.Setup(t => t.Name).Returns("my_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "my_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(MakeFunctionCallChunk("my_tool", new Dictionary<string, object>()))
                    : ToAsyncEnumerable(MakeTextChunk("final response"));
            });

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "do it" }],
            tools: [_toolMock.Object], toolContext: Context));

        Assert.Single(result);
        Assert.Equal("final response", result[0].Content);
        Assert.Equal(2, callCount);
        _toolMock.Verify(t => t.ExecuteAsync(
            It.IsAny<IReadOnlyDictionary<string, object>>(),
            It.Is<LLMToolExecutionContext>(c => c.GuildId == 111UL && c.ChannelId == 222UL),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_FunctionCallWithArgs_PassesArgsToTool()
    {
        // LLM이 전달한 args가 툴에 그대로 전달되는지 확인
        _toolMock.Setup(t => t.Name).Returns("timeout_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "timeout_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(
                It.Is<IReadOnlyDictionary<string, object>>(a => a["userName"].ToString() == "Xeno"),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(MakeFunctionCallChunk("timeout_tool", new Dictionary<string, object> { ["userName"] = "Xeno" }))
                    : ToAsyncEnumerable(MakeTextChunk("done"));
            });

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "timeout Xeno" }],
            tools: [_toolMock.Object], toolContext: Context));

        Assert.Equal("done", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_ExceedsMaxIterations_ThrowsInvalidOperationException()
    {
        // 모든 응답이 FunctionCall이면 MaxToolIterations 초과 후 예외 발생
        var llmOpts = Options.Create(new LLMOptions { MaxTokens = 100, Temperature = 0.5f, MaxToolIterations = 2 });
        var geminiOpts = Options.Create(new GeminiOptions { Model = "test-model", ApiKey = "key" });
        var sut = new GeminiProvider(_streamMock.Object, _pipeMock.Object, geminiOpts, llmOpts,
            new Mock<ILogger<GeminiProvider>>().Object);

        _toolMock.Setup(t => t.Name).Returns("loop_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "loop_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object>>(), It.IsAny<LLMToolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeFunctionCallChunk("loop_tool", new Dictionary<string, object>())));

        // IAsyncEnumerable: 예외는 열거 시 발생
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(sut.GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "loop" }],
                tools: [_toolMock.Object], toolContext: Context)));
    }

    [Fact]
    public void BuildToolDeclarations_ParametersJsonSchema_IsJsonElement()
    {
        // string 전달 시 SDK가 JSON string literal로 직렬화 → API 거부 방지 — JsonElement 타입 검증
        var tool = new Mock<ILLMTool>();
        tool.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = "test_tool", Description = "test", ParametersJsonSchema = "{}"
        });

        var result = GeminiProvider.BuildToolDeclarations([tool.Object]);

        var declaration = result[0].FunctionDeclarations![0];
        var schema = Assert.IsType<JsonElement>(declaration.ParametersJsonSchema);
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
    }

    [Fact]
    public void BuildToolDeclarations_TrimsAndParsesSchemaWithLeadingNewline()
    {
        // raw string literal 앞뒤 \n 포함 케이스 — Trim 후 JsonElement로 파싱됨
        var tool = new Mock<ILLMTool>();
        tool.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = "test_tool", Description = "test",
            ParametersJsonSchema = "\n{\"type\":\"object\"}\n"
        });

        var result = GeminiProvider.BuildToolDeclarations([tool.Object]);

        var declaration = result[0].FunctionDeclarations![0];
        var schema = Assert.IsType<JsonElement>(declaration.ParametersJsonSchema);
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
    }

    [Fact]
    public async Task GenerateAsync_TokensAccumulated_AcrossAllIterations()
    {
        // FunctionCall-only iteration 토큰이 다음 yield로 누적되는지 확인
        // iteration 0: FunctionCall only (100 in, 10 out) → yield 없음, pending 누적
        // iteration 1: text only (150 in, 20 out) → yield final with (100+150, 10+20) = (250, 30)
        _toolMock.Setup(t => t.Name).Returns("my_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "my_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(It.IsAny<IReadOnlyDictionary<string, object>>(), It.IsAny<LLMToolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        GenerateContentResponse MakeChunkWithTokens(string? text, FunctionCall? fc, int promptT, int candidateT)
        {
            var parts = new List<Part>();
            if (text is not null) parts.Add(new Part { Text = text });
            if (fc is not null) parts.Add(new Part { FunctionCall = fc });
            return new GenerateContentResponse
            {
                Candidates = [new Candidate { Content = new Content { Parts = parts } }],
                UsageMetadata = new GenerateContentResponseUsageMetadata
                {
                    PromptTokenCount = promptT,
                    CandidatesTokenCount = candidateT
                }
            };
        }

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(MakeChunkWithTokens(null, new FunctionCall { Name = "my_tool", Args = new() }, 100, 10))
                    : ToAsyncEnumerable(MakeChunkWithTokens("done", null, 150, 20));
            });

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [_toolMock.Object], toolContext: Context));

        // FunctionCall-only iteration 토큰이 최종 응답에 누적됨
        Assert.Single(result);
        Assert.Equal(250, result[0].TokensIn);   // 100 + 150
        Assert.Equal(30,  result[0].TokensOut);  // 10 + 20
    }

    // --- BuildContents ToolCall/ToolResponse ---

    [Fact]
    public void BuildContents_ToolCallRole_MapsToFunctionCallContent()
    {
        // Role.ToolCall → model 역할, FunctionCall Part로 변환되는지 확인
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role         = Role.ToolCall,
                ToolName     = "my_tool",
                ToolArgsJson = """{"userName":"Xeno"}"""
            }
        };

        var result = GeminiProvider.BuildContents(messages);

        Assert.Single(result);
        Assert.Equal("model", result[0].Role);
        Assert.NotNull(result[0].Parts);
        Assert.Single(result[0].Parts!);
        var fc = result[0].Parts![0].FunctionCall;
        Assert.NotNull(fc);
        Assert.Equal("my_tool", fc.Name);
    }

    [Fact]
    public void BuildContents_ToolResponseRole_MapsToFunctionResponseContent()
    {
        // Role.ToolResponse → user 역할, FunctionResponse Part로 변환되는지 확인
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role     = Role.ToolResponse,
                ToolName = "my_tool",
                Content  = """{"success":true}"""
            }
        };

        var result = GeminiProvider.BuildContents(messages);

        Assert.Single(result);
        Assert.Equal("user", result[0].Role);
        Assert.NotNull(result[0].Parts);
        Assert.Single(result[0].Parts!);
        var fr = result[0].Parts![0].FunctionResponse;
        Assert.NotNull(fr);
        Assert.Equal("my_tool", fr.Name);
        Assert.True(fr.Response!.ContainsKey("result"));
    }

    [Fact]
    public async Task GenerateAsync_CollectsToolCallHistory_InLLMResponse()
    {
        // FunctionCall 1회 실행 → ToolCallHistory에 ToolCall + ToolResponse 2개 저장되는지 확인
        _toolMock.Setup(t => t.Name).Returns("my_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "my_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResult { Success = true });

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(MakeFunctionCallChunk("my_tool", new Dictionary<string, object>()))
                    : ToAsyncEnumerable(MakeTextChunk("done"));
            });

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [_toolMock.Object], toolContext: Context));

        // 1 FunctionCall → ToolCall + ToolResponse = 2개
        var history = result[0].ToolCallHistory;
        Assert.Equal(2, history.Count);
        Assert.Equal(Role.ToolCall, history[0].Role);
        Assert.Equal(Role.ToolResponse, history[1].Role);
        Assert.Equal("my_tool", history[0].ToolName);
        Assert.Equal("my_tool", history[1].ToolName);
        // 같은 callId로 쌍 연결 확인
        Assert.NotNull(history[0].CallId);
        Assert.Equal(history[0].CallId, history[1].CallId);
    }

    [Fact]
    public async Task GenerateAsync_ToolThrowsException_ReturnsFailResult_AndContinuesIteration()
    {
        // tool.ExecuteAsync에서 예외 발생 시 ToolResult.Fail로 변환되고 LLM 다음 호출로 이어지는지 확인
        _toolMock.Setup(t => t.Name).Returns("my_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "my_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Missing Permissions"));

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(MakeFunctionCallChunk("my_tool", new Dictionary<string, object>()))
                    : ToAsyncEnumerable(MakeTextChunk("tool failed but I can respond"));
            });

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [_toolMock.Object], toolContext: Context));

        // iteration 중단 없이 최종 응답 반환
        Assert.Equal("tool failed but I can respond", result[0].Content);
        Assert.Equal(2, callCount);

        // ToolCallHistory에 실패 ToolResponse 포함
        var history = result[0].ToolCallHistory;
        Assert.Equal(2, history.Count);
        var toolResponse = history[1];
        Assert.Equal(Role.ToolResponse, toolResponse.Role);
        // 실패 결과가 JSON으로 저장됨
        Assert.Contains("false", toolResponse.Content);
        Assert.Contains("Missing Permissions", toolResponse.Content);
    }

    [Fact]
    public async Task GenerateAsync_ToolThrowsOperationCanceled_Rethrows()
    {
        // OperationCanceledException은 re-throw — iteration 중단
        _toolMock.Setup(t => t.Name).Returns("my_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "my_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeFunctionCallChunk("my_tool", new Dictionary<string, object>())));

        // IAsyncEnumerable: 예외는 열거 시 발생
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CollectAsync(_sut.GenerateAsync(
                [new ChatMessage { Role = Role.User, Content = "go" }],
                tools: [_toolMock.Object], toolContext: Context)));
    }

    [Fact]
    public async Task GenerateAsync_ToolResponse_SerializesRuntimeType_IncludesDerivedFields()
    {
        // 파생 ToolResult 타입을 반환할 때 DB 저장 JSON에 파생 필드가 포함되는지 확인
        // 선언 타입(ToolResult)으로 직렬화하면 파생 필드가 누락됨 — runtime 타입 직렬화 계약 검증

        // 실제 ListChannelUsersResult와 동일한 구조의 파생 타입 구현
        var derivedResult = new ListChannelUsersResult
        {
            Success = true,
            MemberCount = 2,
            Members = ["Alice", "Bob"]
        };

        _toolMock.Setup(t => t.Name).Returns("my_tool");
        _toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition { Name = "my_tool", Description = "d", ParametersJsonSchema = "{}" });
        _toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(derivedResult);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? ToAsyncEnumerable(MakeFunctionCallChunk("my_tool", new Dictionary<string, object>()))
                    : ToAsyncEnumerable(MakeTextChunk("done"));
            });

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "go" }],
            tools: [_toolMock.Object], toolContext: Context));

        var toolResponseContent = result[0].ToolCallHistory
            .First(m => m.Role == Role.ToolResponse).Content;

        // runtime 타입 직렬화 → 파생 필드 포함
        Assert.Contains("memberCount", toolResponseContent);
        Assert.Contains("members", toolResponseContent);
        Assert.Contains("Alice", toolResponseContent);
    }

    [Fact]
    public async Task GenerateAsync_NoToolCalls_ToolCallHistoryIsEmpty()
    {
        // 툴 호출 없는 경우 ToolCallHistory 빈 목록 반환 확인
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeTextChunk("response")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Empty(result[0].ToolCallHistory);
    }
}
