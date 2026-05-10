using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.Services;

public class SemanticMemoryServiceTests
{
    private readonly Mock<ILLMProvider>                _llmMock    = new();
    private readonly Mock<ISemanticMemoryRepository>   _repoMock   = new();
    private readonly Mock<IConversationRepository>     _convMock   = new();
    private readonly Mock<IPromptLoader>               _promptMock = new();

    private SemanticMemoryService CreateSut(
        int traitThreshold    = 20,
        int episodicThreshold = 20)
    {
        _promptMock.Setup(p => p.SemanticMemoryExtractionPrompt).Returns("[EXTRACT]");
        _promptMock.Setup(p => p.SemanticMemoryCompressionPrompt).Returns("[COMPRESS]");

        var opts = Options.Create(new SemanticMemoryOptions
        {
            TraitCompressionThreshold    = traitThreshold,
            EpisodicCompressionThreshold = episodicThreshold
        });
        return new SemanticMemoryService(
            _llmMock.Object,
            _repoMock.Object,
            _convMock.Object,
            _promptMock.Object,
            opts,
            NullLogger<SemanticMemoryService>.Instance);
    }

    // ─── 헬퍼 ────────────────────────────────────────────────────────────────

    private static ConversationContext MakeContext(
        string contextId                    = "ctx001",
        Dictionary<string, ulong>? participants = null,
        List<ulong>? semanticMemoryRefs     = null,
        string? dynamicCacheRef             = null,
        DateTimeOffset? cacheExpiresAt      = null) => new()
    {
        Id           = contextId,
        Messages     =
        [
            new ChatMessage { Role = Role.System,    Content = "sys" },
            new ChatMessage { Role = Role.User,      Content = "hello", SenderName = "Alice" },
            new ChatMessage { Role = Role.Assistant, Content = "hi" }
        ],
        Participants       = participants ?? new Dictionary<string, ulong> { ["Alice"] = 100UL },
        SemanticMemoryRefs = semanticMemoryRefs ?? new List<ulong>(),
        DynamicCacheRef    = dynamicCacheRef,
        CacheExpiresAt     = cacheExpiresAt
    };

    private static string ExtractionJson(
        string subject      = "Alice",
        string[]? traits    = null,
        string[]? episodic  = null) =>
        $$"""
        {
          "users": [{
            "subject": "{{subject}}",
            "traits":   [{{string.Join(",", (traits   ?? []).Select(t => $"\"{t}\""))}}],
            "episodic": [{{string.Join(",", (episodic ?? []).Select(e => $"\"{e}\""))}}]
          }]
        }
        """;

    private void SetupLlm(params string[] responses)
    {
        var queue = new Queue<string>(responses);
        _llmMock.Setup(p => p.GenerateAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<IReadOnlyList<ILLMTool>?>(),
                It.IsAny<LLMToolExecutionContext?>(),
                It.IsAny<CacheHint?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => ToAsyncEnumerable(queue.Dequeue()));
    }

    private static async IAsyncEnumerable<LLMResponse> ToAsyncEnumerable(string content)
    {
        await Task.CompletedTask;
        yield return new LLMResponse { Content = content };
    }

    private void SetupRepoNoMemory()
    {
        _repoMock.Setup(r => r.GetLatestActiveAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSemanticMemory?)null);
    }

    private void SetupRepoWithSnapshot(ulong userId, SemanticMemoryData snapshot)
    {
        _repoMock.Setup(r => r.GetLatestActiveAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSemanticMemory
            {
                Id       = "doc001",
                UserId   = userId,
                Snapshot = snapshot
            });
    }

    // =========================================================
    // 1. 중복 방지
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_RefsNotEmpty_SkipsExtraction()
    {
        var ctx = MakeContext(semanticMemoryRefs: [100UL]);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _llmMock.Verify(p => p.GenerateAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<IReadOnlyList<ILLMTool>?>(),
            It.IsAny<LLMToolExecutionContext?>(),
            It.IsAny<CacheHint?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================
    // 2. 신규 document 생성 + snapshot 누적
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_NoPreviousSnapshot_CreatesDocumentWithExtractedOnly()
    {
        // 이전 active 없음 → snapshot = extracted 그대로
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"], episodic: ["Alice visited Tokyo"]));
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.UserId    == 100UL &&
                m.SessionId == "ctx001" &&
                m.State     == SemanticMemoryState.Active &&
                m.Extracted.Traits.Contains("Alice likes cats") &&
                m.Extracted.Episodic.Contains("Alice visited Tokyo") &&
                m.Snapshot.Traits.Contains("Alice likes cats") &&
                m.Snapshot.Episodic.Contains("Alice visited Tokyo")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_PreviousSnapshot_AccumulatesIntoSnapshot()
    {
        // 이전 active snapshot 있음 → snapshot = 이전 + 신규
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes dogs"]));
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData
        {
            Traits   = ["Alice likes cats"],
            Episodic = ["Alice visited Tokyo"]
        });
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                // extracted: 이번 session만
                m.Extracted.Traits.SequenceEqual(new[] { "Alice likes dogs" }) &&
                m.Extracted.Episodic.Count == 0 &&
                // snapshot: 이전 + 이번 누적
                m.Snapshot.Traits.Contains("Alice likes cats") &&
                m.Snapshot.Traits.Contains("Alice likes dogs") &&
                m.Snapshot.Episodic.Contains("Alice visited Tokyo")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 3. SemanticMemoryRefs 추가 + context 저장
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_SuccessfulUser_AddsToRefsAndSavesContext()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["t"]));
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        Assert.Contains(100UL, ctx.SemanticMemoryRefs);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_SubjectNotFound_SkipsUserAndDoesNotAddToRefs()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Bob"] = 200UL });
        SetupLlm(ExtractionJson("Alice", traits: ["t"]));
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        Assert.Empty(ctx.SemanticMemoryRefs);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<UserSemanticMemory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================
    // 4. Zero users 경계
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_ZeroUsersExtracted_SavesContextAndReturns()
    {
        var ctx = MakeContext();
        SetupLlm("""{"users":[]}""");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<UserSemanticMemory>(), It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_EmptyExtracted_CreatesDocumentWithEmptyData()
    {
        // subject는 있으나 traits/episodic 모두 빈 배열
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice")); // traits/episodic 모두 null → 빈 배열
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        // document 생성, refs에 추가
        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.UserId == 100UL &&
                m.Extracted.Traits.Count == 0 &&
                m.Snapshot.Traits.Count  == 0),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(100UL, ctx.SemanticMemoryRefs);
    }

    // =========================================================
    // 5. 압축 — trait
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_TraitBelowThreshold_NoCompression()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["new trait"]));
        // 이전 snapshot: 2개 traits → threshold=5 미달
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData
        {
            Traits = ["t1", "t2"]
        });
        var sut = CreateSut(traitThreshold: 5);

        await sut.ExtractAndSaveAsync(ctx);

        // LLM 1회만 호출 (extraction만, compression 없음)
        _llmMock.Verify(p => p.GenerateAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<IReadOnlyList<ILLMTool>?>(),
            It.IsAny<LLMToolExecutionContext?>(),
            It.IsAny<CacheHint?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_TraitThresholdReached_CompressesSnapshotTraits()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        // 1st LLM: extraction, 2nd: compression
        SetupLlm(
            ExtractionJson("Alice", traits: ["new trait"]),
            """["Alice is a cat lover."]"""
        );
        // 이전 snapshot: 2개 traits, threshold=2 → 신규 추가 후 3개 → 압축 트리거
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData
        {
            Traits = ["t1", "t2"]
        });
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.Snapshot.Traits.Count == 1 &&
                m.Snapshot.Traits[0]    == "Alice is a cat lover."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 6. 압축 — episodic
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_EpisodicThresholdReached_CompressesSnapshotEpisodic()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", episodic: ["new episode"]),
            """["Alice has travelled to Tokyo."]"""
        );
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData
        {
            Episodic = ["ep1", "ep2"]
        });
        var sut = CreateSut(episodicThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.Snapshot.Episodic.Count == 1 &&
                m.Snapshot.Episodic[0]    == "Alice has travelled to Tokyo."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 7. 압축 실패 시 원본 보존
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_CompressionFails_PreservesOriginalTraits()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        // 압축 LLM이 빈 배열 반환 → 원본 보존
        SetupLlm(
            ExtractionJson("Alice", traits: ["new trait"]),
            "[]"  // empty → 원본 유지
        );
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData
        {
            Traits = ["t1", "t2"]
        });
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        // 원본 3개 보존
        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.Snapshot.Traits.Count == 3 &&
                m.Snapshot.Traits.Contains("t1") &&
                m.Snapshot.Traits.Contains("t2") &&
                m.Snapshot.Traits.Contains("new trait")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_CompressionInvalidJson_PreservesOriginalTraits()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", traits: ["new"]),
            "NOT VALID JSON"
        );
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData
        {
            Traits = ["t1", "t2"]
        });
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m => m.Snapshot.Traits.Count == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 8. 압축 fenced JSON 처리
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_CompressionMarkdownFence_ParsedCorrectly()
    {
        var fenced = """
            ```json
            ["Alice is a cat lover."]
            ```
            """;
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["new"]), fenced);
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData { Traits = ["t1", "t2"] });
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.Snapshot.Traits.Count == 1 &&
                m.Snapshot.Traits[0]    == "Alice is a cat lover."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_CompressionMalformedFence_PreservesOriginals()
    {
        // fence 시작은 있지만 newline 없음 → StripMarkdownFence fallback → 원본 보존
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", traits: ["new trait"]),
            "```notjson"  // malformed: starts with ``` but no newline
        );
        SetupRepoWithSnapshot(100UL, new SemanticMemoryData { Traits = ["t1", "t2"] });
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        // parse fails → compressed = [] → originals preserved
        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m => m.Snapshot.Traits.Count == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 9. Extraction JSON 경계
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_MarkdownFencedExtractionJson_ParsedCorrectly()
    {
        var fenced = """
            ```json
            {"users":[{"subject":"Alice","traits":["cat lover"],"episodic":[]}]}
            ```
            """;
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(fenced);
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m => m.Snapshot.Traits.Contains("cat lover")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_MalformedFenceExtraction_TreatsAsRaw()
    {
        // fence 시작은 있지만 newline 없음 → StripMarkdownFence fallback → JSON 파싱 실패 → context 저장
        var ctx = MakeContext();
        SetupLlm("```notjson");  // malformed: starts with ``` but no newline
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<UserSemanticMemory>(), It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_InvalidExtractionJson_SavesContextAndReturns()
    {
        var ctx = MakeContext();
        SetupLlm("NOT VALID JSON {{{");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<UserSemanticMemory>(), It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_MissingUsersProperty_SavesContextAndReturns()
    {
        var ctx = MakeContext();
        SetupLlm("""{"other":"value"}""");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<UserSemanticMemory>(), It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_EmptySubject_FilteredOut()
    {
        var ctx = MakeContext();
        SetupLlm("""{"users":[{"subject":"","traits":["t"],"episodic":[]}]}""");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<UserSemanticMemory>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_MissingEpisodicField_ExtractsTraitsOnly()
    {
        // episodic 키 누락 → ReadStringArray missing branch → episodic=[]
        var partialJson = """{"users":[{"subject":"Alice","traits":["Alice likes cats"]}]}""";
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(partialJson);
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.CreateAsync(
            It.Is<UserSemanticMemory>(m =>
                m.Snapshot.Traits.Contains("Alice likes cats") &&
                m.Snapshot.Episodic.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 10. CacheHint 전달
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_ValidCache_PassesCacheHint()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var ctx     = MakeContext(dynamicCacheRef: "cache123", cacheExpiresAt: expires);
        ctx.CachedMessageCount = 3;
        SetupLlm(ExtractionJson("Alice", traits: ["t"]));
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _llmMock.Verify(p => p.GenerateAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<IReadOnlyList<ILLMTool>?>(),
            It.IsAny<LLMToolExecutionContext?>(),
            It.Is<CacheHint?>(h => h != null && h.CachedContentName == "cache123"),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_ExpiredCache_NoCacheHint()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        var ctx     = MakeContext(dynamicCacheRef: "cache123", cacheExpiresAt: expires);
        SetupLlm(ExtractionJson("Alice", traits: ["t"]));
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _llmMock.Verify(p => p.GenerateAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<IReadOnlyList<ILLMTool>?>(),
            It.IsAny<LLMToolExecutionContext?>(),
            It.Is<CacheHint?>(h => h == null),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_NullCacheRef_NoCacheHint()
    {
        var ctx = MakeContext(dynamicCacheRef: null);
        SetupLlm(ExtractionJson("Alice", traits: ["t"]));
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _llmMock.Verify(p => p.GenerateAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<IReadOnlyList<ILLMTool>?>(),
            It.IsAny<LLMToolExecutionContext?>(),
            It.Is<CacheHint?>(h => h == null),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_NullCacheExpiresAtWithCacheRef_NoCacheHint()
    {
        var ctx = MakeContext(dynamicCacheRef: "cache123", cacheExpiresAt: null);
        SetupLlm(ExtractionJson("Alice", traits: ["t"]));
        SetupRepoNoMemory();
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _llmMock.Verify(p => p.GenerateAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<IReadOnlyList<ILLMTool>?>(),
            It.IsAny<LLMToolExecutionContext?>(),
            It.Is<CacheHint?>(h => h == null),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 11. GetLatestSnapshotAsync
    // =========================================================

    [Fact]
    public async Task GetLatestSnapshotAsync_NoActiveDocument_ReturnsNull()
    {
        _repoMock.Setup(r => r.GetLatestActiveAsync(999UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSemanticMemory?)null);
        var sut = CreateSut();

        var result = await sut.GetLatestSnapshotAsync(999UL);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestSnapshotAsync_ActiveExists_ReturnsSnapshot()
    {
        var snapshot = new SemanticMemoryData
        {
            Traits   = ["Alice likes cats"],
            Episodic = ["Alice visited Tokyo"]
        };
        _repoMock.Setup(r => r.GetLatestActiveAsync(100UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSemanticMemory { UserId = 100UL, Snapshot = snapshot });
        var sut = CreateSut();

        var result = await sut.GetLatestSnapshotAsync(100UL);

        Assert.NotNull(result);
        Assert.Contains("Alice likes cats",   result.Traits);
        Assert.Contains("Alice visited Tokyo", result.Episodic);
    }
}
