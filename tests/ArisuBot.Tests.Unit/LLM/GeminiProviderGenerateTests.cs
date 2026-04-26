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

public class GeminiProviderGenerateTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();
    private readonly Mock<ILlmMonitorServer> _pipeMock = new();
    private readonly Mock<IErrorLogger> _errorLoggerMock = new();
    private readonly GeminiProvider _sut;

    public GeminiProviderGenerateTests()
    {
        var geminiOpts = Options.Create(new GeminiOptions { Model = "test-model", ApiKey = "key" });
        var llmOpts = Options.Create(new LLMOptions { MaxTokens = 100, Temperature = 0.5f });
        _sut = new GeminiProvider(_streamMock.Object, _pipeMock.Object, _errorLoggerMock.Object,
            geminiOpts, llmOpts, new Mock<ILogger<GeminiProvider>>().Object);
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

    private static GenerateContentResponse MakeChunk(string? text,
        int promptTokens = 0, int candidateTokens = 0, int cachedTokens = 0)
    {
        var response = new GenerateContentResponse();
        if (text is not null)
        {
            response.Candidates =
            [
                new Candidate
                {
                    Content = new Content { Parts = [new Part { Text = text }] }
                }
            ];
        }
        if (promptTokens > 0 || candidateTokens > 0 || cachedTokens > 0)
        {
            response.UsageMetadata = new GenerateContentResponseUsageMetadata
            {
                PromptTokenCount = promptTokens,
                CandidatesTokenCount = candidateTokens,
                CachedContentTokenCount = cachedTokens
            };
        }
        return response;
    }

    [Fact]
    public async Task GenerateAsync_AccumulatesTextChunks()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("hello "), MakeChunk("world")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Single(result);
        Assert.Equal("hello world", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsProviderName_Gemini()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("ok")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Equal("Gemini", result[0].ProviderName);
    }

    [Fact]
    public async Task GenerateAsync_PopulatesTokenCounts_FromLastChunkUsageMetadata()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(
                MakeChunk("part1"),
                MakeChunk("part2", promptTokens: 100, candidateTokens: 50, cachedTokens: 20)));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Equal(100, result[0].TokensIn);
        Assert.Equal(50,  result[0].TokensOut);
        Assert.Equal(20,  result[0].TokensCachedIn);
    }

    [Fact]
    public async Task GenerateAsync_ZeroTokens_WhenNoUsageMetadata()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("text")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Equal(0, result[0].TokensIn);
        Assert.Equal(0, result[0].TokensOut);
        Assert.Equal(0, result[0].TokensCachedIn);
    }

    [Fact]
    public async Task GenerateAsync_EmptyContent_WhenNoTextChunks()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk(null, promptTokens: 10, candidateTokens: 0)));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Equal(string.Empty, result[0].Content);
    }

    // --- Tool loop: model Content 재구성 검증 ---

    private static (Mock<ILLMTool> tool, LLMToolExecutionContext ctx) MakeSingleTool(string name = "test_tool")
    {
        var toolMock = new Mock<ILLMTool>();
        toolMock.Setup(t => t.Name).Returns(name);
        toolMock.Setup(t => t.Definition).Returns(new LLMToolDefinition
        {
            Name = name, Description = "x",
            ParametersJsonSchema = """{"type":"object","properties":{}}"""
        });
        toolMock.Setup(t => t.ExecuteAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<LLMToolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Fail("test"));
        return (toolMock, new LLMToolExecutionContext(1, 1));
    }

    [Fact]
    public async Task GenerateAsync_ToolLoop_TextAlongsideFunctionCall_TextPreservedInModelContent()
    {
        var fc = new FunctionCall { Name = "test_tool", Args = new Dictionary<string, object>() };
        var firstChunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [new Part { Text = "let me think" }, new Part { FunctionCall = fc }]
                    }
                }
            ]
        };

        List<Content>? capturedContents = null;
        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns((string m, IEnumerable<Content> c, GenerateContentConfig cfg) =>
            {
                callCount++;
                if (callCount == 1) return ToAsyncEnumerable(firstChunk);
                capturedContents = c.ToList();
                return ToAsyncEnumerable(MakeChunk("done"));
            });

        var (toolMock, ctx) = MakeSingleTool();
        await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [toolMock.Object], toolContext: ctx));

        Assert.NotNull(capturedContents);
        var modelContent = capturedContents!.FirstOrDefault(c => c.Role == "model" && (c.Parts?.Any(p => p.FunctionCall != null) ?? false));
        Assert.NotNull(modelContent);
        // text Part이 FunctionCall과 함께 model Content에 포함되어야 함
        Assert.Contains(modelContent!.Parts!, p => p.Text == "let me think");
    }

    [Fact]
    public async Task GenerateAsync_ToolLoop_OpaquePartAlongsideFunctionCall_PreservedInModelContent()
    {
        // thought_signature 등 text/FunctionCall 이외 Part 보존 검증
        var opaquePart = new Part(); // non-text, non-FunctionCall — thought Part 대역
        var fc = new FunctionCall { Name = "test_tool", Args = new Dictionary<string, object>() };
        var firstChunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [opaquePart, new Part { FunctionCall = fc }]
                    }
                }
            ]
        };

        List<Content>? capturedContents = null;
        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns((string m, IEnumerable<Content> c, GenerateContentConfig cfg) =>
            {
                callCount++;
                if (callCount == 1) return ToAsyncEnumerable(firstChunk);
                capturedContents = c.ToList();
                return ToAsyncEnumerable(MakeChunk("done"));
            });

        var (toolMock, ctx) = MakeSingleTool();
        await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [toolMock.Object], toolContext: ctx));

        Assert.NotNull(capturedContents);
        var modelContent = capturedContents!.FirstOrDefault(c => c.Role == "model" && (c.Parts?.Any(p => p.FunctionCall != null) ?? false));
        Assert.NotNull(modelContent);
        // opaque Part이 model Content에 포함되어야 함 — thought_signature 누락 방지
        Assert.Contains(modelContent!.Parts!, p => ReferenceEquals(p, opaquePart));
        Assert.Contains(modelContent!.Parts!, p => p.FunctionCall?.Name == "test_tool");
    }

    [Fact]
    public async Task GenerateAsync_ToolLoop_TextAlongsideFunctionCall_YieldsIntermediateThenFinal()
    {
        // iteration 0: text + FunctionCall 동시 도착 → 중간 응답 yield 후 툴 실행
        // iteration 1: 최종 텍스트만 도착 → 최종 응답 yield
        var fc = new FunctionCall { Name = "test_tool", Args = new Dictionary<string, object>() };
        var firstChunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [new Part { Text = "let me think" }, new Part { FunctionCall = fc }]
                    }
                }
            ],
            UsageMetadata = new GenerateContentResponseUsageMetadata
            {
                PromptTokenCount = 10, CandidatesTokenCount = 5
            }
        };
        var finalChunk = MakeChunk("done", promptTokens: 20, candidateTokens: 8);

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns((string m, IEnumerable<Content> c, GenerateContentConfig cfg) =>
            {
                callCount++;
                return callCount == 1 ? ToAsyncEnumerable(firstChunk) : ToAsyncEnumerable(finalChunk);
            });

        var (toolMock, ctx) = MakeSingleTool();
        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [toolMock.Object], toolContext: ctx));

        // 중간 응답 + 최종 응답 = 2개
        Assert.Equal(2, result.Count);

        // 중간 응답: iteration 0 텍스트 + iteration 0 토큰, ToolCallHistory 없음
        Assert.Equal("let me think", result[0].Content);
        Assert.Equal(10, result[0].TokensIn);
        Assert.Equal(5,  result[0].TokensOut);
        Assert.Empty(result[0].ToolCallHistory);

        // 최종 응답: iteration 1 텍스트 + iteration 1 토큰 + ToolCallHistory(툴 실행 이력)
        Assert.Equal("done", result[1].Content);
        Assert.Equal(20, result[1].TokensIn);
        Assert.Equal(8,  result[1].TokensOut);
        Assert.NotEmpty(result[1].ToolCallHistory);
    }

    // --- thought_signature(ProviderMetadataJson) 보존 검증 ---

    [Fact]
    public async Task GenerateAsync_PopulatesProviderMetadataJson_WhenThoughtPartPresent()
    {
        // thinking 모델이 보내는 non-text/non-FunctionCall Part(예: thought_signature)가
        // 최종 LLMResponse.ProviderMetadataJson에 직렬화되어 담겨야 DB 보존 가능
        var thoughtPart = new Part(); // 빈 Part이지만 non-text/non-FunctionCall → thoughtParts에 포함
        var chunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [new Part { Text = "final" }, thoughtPart]
                    }
                }
            ]
        };

        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(chunk));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Single(result);
        Assert.NotNull(result[0].ProviderMetadataJson);
        // JSON array 형태로 직렬화됨
        Assert.StartsWith("[", result[0].ProviderMetadataJson);
    }

    [Fact]
    public async Task GenerateAsync_ProviderMetadataJson_IncludesTextPart_WhenOnlyText()
    {
        // 새 정책: text Part도 metadata에 포함 — 도착 순서·byte-identical 보존이 cache hit 조건
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("only text")));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.NotNull(result[0].ProviderMetadataJson);
        Assert.Contains("only text", result[0].ProviderMetadataJson);
    }

    [Fact]
    public async Task GenerateAsync_ProviderMetadataJson_IsNull_WhenNoParts()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk(null, promptTokens: 5)));

        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]));

        Assert.Null(result[0].ProviderMetadataJson);
    }

    [Fact]
    public async Task GenerateAsync_ToolLoop_AttachesProviderMetadataJson_ToFirstToolCallOnly()
    {
        // 한 iteration에 여러 FunctionCall + thought Part 도착 시 thought는 첫 ToolCall 메시지에만 첨부
        var thoughtPart = new Part();
        var fc1 = new FunctionCall { Name = "test_tool", Args = new Dictionary<string, object>() };
        var fc2 = new FunctionCall { Name = "test_tool", Args = new Dictionary<string, object>() };
        var firstChunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Parts = [thoughtPart, new Part { FunctionCall = fc1 }, new Part { FunctionCall = fc2 }]
                    }
                }
            ]
        };

        var callCount = 0;
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns((string m, IEnumerable<Content> c, GenerateContentConfig cfg) =>
            {
                callCount++;
                return callCount == 1 ? ToAsyncEnumerable(firstChunk) : ToAsyncEnumerable(MakeChunk("done"));
            });

        var (toolMock, ctx) = MakeSingleTool();
        var result = await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            tools: [toolMock.Object], toolContext: ctx));

        var toolCalls = result.SelectMany(r => r.ToolCallHistory)
            .Where(m => m.Role == Role.ToolCall).ToList();

        Assert.Equal(2, toolCalls.Count);
        // 첫 ToolCall: metadata 있음
        Assert.NotNull(toolCalls[0].ProviderMetadataJson);
        // 두 번째 ToolCall: metadata 없음 — 중복 방지
        Assert.Null(toolCalls[1].ProviderMetadataJson);
    }

    // --- BuildContents: ProviderMetadataJson 복원 검증 ---

    [Fact]
    public void BuildContents_AssistantWithMetadata_UsesMetadataPartsAsContent()
    {
        // metadata는 text 포함 모든 non-FunctionCall Part를 도착 순서대로 보유 → m.Content는 무시되고 metadata가 그대로 Parts가 됨.
        // 두 번째 Part 는 ThoughtSignature 가진 thought Part → 순수 text 가 아니므로 text 병합 대상에서 제외되어 그대로 유지됨.
        var metadataJson = """[{"text":"actual-text"},{"text":"thought-placeholder","thoughtSignature":"AQID"}]""";
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.Assistant, Content = "hi-ignored", ProviderMetadataJson = metadataJson }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var modelContent = Assert.Single(contents);
        Assert.Equal("model", modelContent.Role);
        Assert.Equal(2, modelContent.Parts!.Count);
        Assert.Equal("actual-text", modelContent.Parts[0].Text);
        Assert.Equal("thought-placeholder", modelContent.Parts[1].Text);
        Assert.NotNull(modelContent.Parts[1].ThoughtSignature);
    }

    [Fact]
    public void BuildContents_AssistantWithoutMetadata_OnlyTextPart()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.Assistant, Content = "hi" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var modelContent = Assert.Single(contents);
        Assert.Single(modelContent.Parts!);
        Assert.Equal("hi", modelContent.Parts![0].Text);
    }

    [Fact]
    public void BuildContents_ToolCallWithMetadata_RestoresPartsBeforeFunctionCall()
    {
        var metadataJson = """[{"text":"thought-placeholder"}]""";
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role                 = Role.ToolCall,
                ToolName             = "my_tool",
                ToolArgsJson         = "{}",
                ProviderMetadataJson = metadataJson
            }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var modelContent = Assert.Single(contents);
        Assert.Equal("model", modelContent.Role);
        Assert.Equal(2, modelContent.Parts!.Count);
        // metadata Part 먼저 + FunctionCall 뒤
        Assert.Equal("thought-placeholder", modelContent.Parts[0].Text);
        Assert.NotNull(modelContent.Parts[1].FunctionCall);
        Assert.Equal("my_tool", modelContent.Parts[1].FunctionCall!.Name);
    }

    [Fact]
    public void BuildContents_UserMessage_IgnoresProviderMetadataJson()
    {
        // User 메시지에는 적용되지 않음 — Assistant만 대상
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = "hi", ProviderMetadataJson = """[{"text":"ignored"}]""" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var userContent = Assert.Single(contents);
        Assert.Equal("user", userContent.Role);
        Assert.Single(userContent.Parts!);
        Assert.Equal("hi", userContent.Parts![0].Text);
    }

    [Fact]
    public void BuildContents_ConsecutiveUserMessages_MergedIntoSingleContent()
    {
        // 연속된 User 메시지 → 하나 Content, Parts concat. 순수 text 는 추가로 text 병합되어 단일 Part 로 합쳐짐
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = "first" },
            new() { Role = Role.User, Content = "second" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var single = Assert.Single(contents);
        Assert.Equal("user", single.Role);
        var part = Assert.Single(single.Parts!);
        Assert.Equal("firstsecond", part.Text);
    }

    [Fact]
    public void BuildContents_ConsecutiveAssistantMessages_MergedIntoSingleContent()
    {
        // 연속된 Assistant 메시지 → 하나 Content, 순수 text 병합
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.Assistant, Content = "a1" },
            new() { Role = Role.Assistant, Content = "a2" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var single = Assert.Single(contents);
        Assert.Equal("model", single.Role);
        var part = Assert.Single(single.Parts!);
        Assert.Equal("a1a2", part.Text);
    }

    [Fact]
    public void BuildContents_AlternatingRoles_NotMerged()
    {
        // User-Assistant-User → 3개 Content 유지
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = "u1" },
            new() { Role = Role.Assistant, Content = "a1" },
            new() { Role = Role.User, Content = "u2" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        Assert.Equal(3, contents.Count);
        Assert.Equal("user", contents[0].Role);
        Assert.Equal("model", contents[1].Role);
        Assert.Equal("user", contents[2].Role);
    }

    [Fact]
    public void BuildContents_AssistantThenToolCall_MergedAsModelRole()
    {
        // Assistant(text) + ToolCall → 둘 다 model role → 병합. Parts: [text, FunctionCall]
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.Assistant, Content = "let me check" },
            new() { Role = Role.ToolCall, ToolName = "test_tool", ToolArgsJson = """{"x":1}""" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        var single = Assert.Single(contents);
        Assert.Equal("model", single.Role);
        Assert.Equal(2, single.Parts!.Count);
        Assert.Equal("let me check", single.Parts[0].Text);
        Assert.NotNull(single.Parts[1].FunctionCall);
        Assert.Equal("test_tool", single.Parts[1].FunctionCall!.Name);
    }

    [Fact]
    public void BuildContents_ToolCallThenToolResponse_NotMerged()
    {
        // ToolCall(model) + ToolResponse(user) → role 다름 → 분리 유지
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.ToolCall, ToolName = "test_tool", ToolArgsJson = """{"x":1}""" },
            new() { Role = Role.ToolResponse, ToolName = "test_tool", Content = """{"ok":true}""" }
        };

        var contents = GeminiProvider.BuildContents(messages);

        Assert.Equal(2, contents.Count);
        Assert.Equal("model", contents[0].Role);
        Assert.Equal("user", contents[1].Role);
    }

    [Fact]
    public void MergeConsecutiveTextParts_PureTexts_Concatenated()
    {
        // 연속 순수 text Part → 하나로 연결
        var parts = new List<Part>
        {
            new() { Text = "hello " },
            new() { Text = "world" }
        };

        var merged = GeminiProvider.MergeConsecutiveTextParts(parts);

        var single = Assert.Single(merged);
        Assert.Equal("hello world", single.Text);
    }

    [Fact]
    public void MergeConsecutiveTextParts_TextThenFunctionCall_NotMerged()
    {
        // text + FunctionCall → 서로 다른 종류 → 분리 유지
        var parts = new List<Part>
        {
            new() { Text = "calling" },
            new() { FunctionCall = new FunctionCall { Name = "t", Args = new Dictionary<string, object>() } }
        };

        var merged = GeminiProvider.MergeConsecutiveTextParts(parts);

        Assert.Equal(2, merged.Count);
        Assert.Equal("calling", merged[0].Text);
        Assert.NotNull(merged[1].FunctionCall);
    }

    [Fact]
    public void MergeConsecutiveTextParts_TextWithThoughtSignature_NotMerged()
    {
        // ThoughtSignature 가진 Part 는 병합 대상 제외 (cache prefix 보존)
        var parts = new List<Part>
        {
            new() { Text = "a", ThoughtSignature = new byte[] { 1, 2, 3 } },
            new() { Text = "b" }
        };

        var merged = GeminiProvider.MergeConsecutiveTextParts(parts);

        Assert.Equal(2, merged.Count);
        Assert.Equal("a", merged[0].Text);
        Assert.NotNull(merged[0].ThoughtSignature);
        Assert.Equal("b", merged[1].Text);
    }

    [Fact]
    public void MergeConsecutiveTextParts_ThreePureTexts_CollapsedToOne()
    {
        // 3개 이상 연속 → 누적 연결
        var parts = new List<Part>
        {
            new() { Text = "[고" },
            new() { Text = "개를 끄덕이며] " },
            new() { Text = "네, 좋아요" }
        };

        var merged = GeminiProvider.MergeConsecutiveTextParts(parts);

        var single = Assert.Single(merged);
        Assert.Equal("[고개를 끄덕이며] 네, 좋아요", single.Text);
    }

    // --- ILlmMonitorServer emit 검증 ---

    [Fact]
    public async Task GenerateAsync_EmitsTokenUsageEvent_OnCompletion()
    {
        // 응답 완료 시 contextId + 토큰 수를 담은 TokenUsageEvent가 emit되어야 함
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("hello", promptTokens: 50, candidateTokens: 20)));

        await CollectAsync(_sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }],
            contextId: "ctx-123"));

        _pipeMock.Verify(p => p.Emit(It.Is<TokenUsageEvent>(e =>
            e.ContextId == "ctx-123" && e.TokensIn == 50 && e.TokensOut == 20)), Times.Once);
    }
}
