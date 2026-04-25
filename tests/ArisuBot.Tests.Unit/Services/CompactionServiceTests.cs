using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.Services;

public class CompactionServiceTests
{
    private readonly Mock<ILLMProvider>            _llmMock      = new();
    private readonly Mock<IConversationRepository> _repoMock     = new();
    private readonly Mock<IPromptLoader>           _promptMock   = new();

    private CompactionService CreateSut(int recentCount = 20)
    {
        var options = Options.Create(new CompactionOptions
        {
            InjectionRecentMessageCount = recentCount
        });
        return new CompactionService(
            _llmMock.Object,
            _repoMock.Object,
            _promptMock.Object,
            options,
            NullLogger<CompactionService>.Instance);
    }

    private static ConversationContext MakeContext(
        ulong targetId = 1,
        ArisuBot.Core.Models.ContextType type = ArisuBot.Core.Models.ContextType.Channel)
    {
        return new ConversationContext
        {
            Id       = "ctx001",
            TargetId = targetId,
            Type     = type,
            Messages =
            [
                new ChatMessage { Role = Role.System, Content = "sys" },
                new ChatMessage { Role = Role.User, Content = "persona", SenderName = null },
                new ChatMessage { Role = Role.User, Content = "msg1", SenderName = "Alice" },
                new ChatMessage { Role = Role.Assistant, Content = "resp1" },
                new ChatMessage { Role = Role.User, Content = "msg2", SenderName = "Bob" }
            ]
        };
    }

    private static string ValidJson(string summary = "test summary", int factCount = 1)
    {
        var facts = Enumerable.Range(0, factCount)
            .Select(i => "{" + $"\"content\": \"fact{i}\", \"subject\": \"Alice\", \"category\": \"preference\"" + "}");
        return "{" + $"\"facts\": [{string.Join(",", facts)}], \"summary\": \"{summary}\"" + "}";
    }

    private void SetupLlm(string response)
    {
        _llmMock.Setup(p => p.GenerateAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<IReadOnlyList<ILLMTool>?>(),
                It.IsAny<LLMToolExecutionContext?>(),
                It.IsAny<CacheHint?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(new LLMResponse { Content = response }));
    }

    private static async IAsyncEnumerable<LLMResponse> ToAsyncEnumerable(LLMResponse response)
    {
        await Task.CompletedTask;
        yield return response;
    }

    // --- LLM 호출 검증 ---

    [Fact]
    public async Task RunAsync_CallsLlm_WithCompactionPromptAppended()
    {
        var context = MakeContext();
        _promptMock.Setup(p => p.CompactionPrompt).Returns("COMPACT_PROMPT");
        SetupLlm(ValidJson());

        IEnumerable<ChatMessage>? capturedMessages = null;
        _llmMock.Setup(p => p.GenerateAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                null, null, null, null,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, IReadOnlyList<ILLMTool>?, LLMToolExecutionContext?,
                CacheHint?, string?, string?, CancellationToken>(
                (msgs, _, _, _, _, _, _) => capturedMessages = msgs.ToList())
            .Returns(ToAsyncEnumerable(new LLMResponse { Content = ValidJson() }));

        var newCtx = new ConversationContext { Id = "new" };
        _repoMock.Setup(r => r.CreateNewSessionAsync(It.IsAny<ulong>(), It.IsAny<ArisuBot.Core.Models.ContextType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newCtx);
        _repoMock.Setup(r => r.SaveContextAsync(It.IsAny<ConversationContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateSut().RunAsync(context);

        Assert.NotNull(capturedMessages);
        var msgList = capturedMessages!.ToList();
        // 마지막 메시지가 compaction prompt
        Assert.Equal(Role.User, msgList[^1].Role);
        Assert.Equal("COMPACT_PROMPT", msgList[^1].Content);
    }

    // --- JSON 파싱 검증 ---

    [Fact]
    public async Task RunAsync_ParsesJsonResult_AndSavesToOldSession()
    {
        var context = MakeContext();
        _promptMock.Setup(p => p.CompactionPrompt).Returns("COMPACT");
        SetupLlm(ValidJson("my summary", 2));

        ConversationContext? savedContext = null;
        _repoMock.Setup(r => r.SaveContextAsync(It.IsAny<ConversationContext>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationContext, CancellationToken>((ctx, _) =>
            {
                if (ctx.Id == "ctx001") savedContext = ctx;
            })
            .Returns(Task.CompletedTask);

        var newCtx = new ConversationContext { Id = "new" };
        _repoMock.Setup(r => r.CreateNewSessionAsync(It.IsAny<ulong>(), It.IsAny<ArisuBot.Core.Models.ContextType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newCtx);

        await CreateSut().RunAsync(context);

        Assert.NotNull(savedContext);
        Assert.Equal("my summary", savedContext!.CompactionSummary);
        Assert.Equal(2, savedContext.CompactionFacts!.Count);
        Assert.NotNull(savedContext.LastCompactedAt);
    }

    [Fact]
    public async Task RunAsync_HandlesMalformedJson_GracefullyWithEmptyResult()
    {
        var context = MakeContext();
        _promptMock.Setup(p => p.CompactionPrompt).Returns("COMPACT");
        SetupLlm("NOT VALID JSON {{{");

        ConversationContext? savedContext = null;
        _repoMock.Setup(r => r.SaveContextAsync(It.IsAny<ConversationContext>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationContext, CancellationToken>((ctx, _) =>
            {
                if (ctx.Id == "ctx001") savedContext = ctx;
            })
            .Returns(Task.CompletedTask);

        var newCtx = new ConversationContext { Id = "new" };
        _repoMock.Setup(r => r.CreateNewSessionAsync(It.IsAny<ulong>(), It.IsAny<ArisuBot.Core.Models.ContextType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newCtx);

        // 예외 없이 완료 — empty result로 계속 진행
        await CreateSut().RunAsync(context);

        Assert.NotNull(savedContext);
        Assert.Equal("", savedContext!.CompactionSummary);
        Assert.Empty(savedContext.CompactionFacts!);
    }

    // --- new session 구조 검증 ---

    [Fact]
    public async Task RunAsync_NewSession_StartsWithProtectedMessages()
    {
        var context = MakeContext();
        _promptMock.Setup(p => p.CompactionPrompt).Returns("COMPACT");
        SetupLlm(ValidJson());

        var newCtx = new ConversationContext { Id = "new" };
        _repoMock.Setup(r => r.CreateNewSessionAsync(It.IsAny<ulong>(), It.IsAny<ArisuBot.Core.Models.ContextType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newCtx);
        _repoMock.Setup(r => r.SaveContextAsync(It.IsAny<ConversationContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateSut().RunAsync(context);

        // [System] + [User,Persona] + [Assistant,synthetic] + recent messages
        Assert.True(result.Messages.Count >= 3);
        Assert.Equal(Role.System, result.Messages[0].Role);
        Assert.Equal(Role.User, result.Messages[1].Role);
        Assert.Null(result.Messages[1].SenderName); // Persona: SenderName == null
        Assert.Equal(Role.Assistant, result.Messages[2].Role); // synthetic
    }

    [Fact]
    public async Task RunAsync_NewSession_SyntheticAssistantContainsSummary()
    {
        var context = MakeContext();
        _promptMock.Setup(p => p.CompactionPrompt).Returns("COMPACT");
        SetupLlm(ValidJson("the big summary"));

        var newCtx = new ConversationContext { Id = "new" };
        _repoMock.Setup(r => r.CreateNewSessionAsync(It.IsAny<ulong>(), It.IsAny<ArisuBot.Core.Models.ContextType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newCtx);
        _repoMock.Setup(r => r.SaveContextAsync(It.IsAny<ConversationContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateSut().RunAsync(context);

        var syntheticMsg = result.Messages.FirstOrDefault(m => m.Role == Role.Assistant);
        Assert.NotNull(syntheticMsg);
        Assert.Contains("the big summary", syntheticMsg!.Content);
    }

    [Fact]
    public async Task RunAsync_NewSession_RecentMessagesLimitedToCount()
    {
        // context에 비protected 메시지 10개 생성
        var context = MakeContext();
        for (var i = 0; i < 8; i++)
        {
            context.Messages.Add(new ChatMessage { Role = Role.User, Content = $"msg{i}", SenderName = "Alice" });
            context.Messages.Add(new ChatMessage { Role = Role.Assistant, Content = $"resp{i}" });
        }

        _promptMock.Setup(p => p.CompactionPrompt).Returns("COMPACT");
        SetupLlm(ValidJson());

        var newCtx = new ConversationContext { Id = "new" };
        _repoMock.Setup(r => r.CreateNewSessionAsync(It.IsAny<ulong>(), It.IsAny<ArisuBot.Core.Models.ContextType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newCtx);
        _repoMock.Setup(r => r.SaveContextAsync(It.IsAny<ConversationContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateSut(recentCount: 5).RunAsync(context);

        // protected(2) + synthetic(1) + recent(5) = 8
        Assert.Equal(8, result.Messages.Count);
    }
}
