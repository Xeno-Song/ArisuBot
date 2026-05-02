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
    private readonly Mock<ILLMProvider>                _llmMock  = new();
    private readonly Mock<ISemanticMemoryRepository>   _repoMock = new();
    private readonly Mock<IConversationRepository>     _convMock = new();
    private readonly Mock<IPromptLoader>               _promptMock = new();

    private SemanticMemoryService CreateSut(
        int traitThreshold   = 20,
        int eventThreshold   = 20,
        int episodeThreshold = 20)
    {
        _promptMock.Setup(p => p.SemanticMemoryExtractionPrompt).Returns("[EXTRACT]");
        _promptMock.Setup(p => p.SemanticMemoryCompressionPrompt).Returns("[COMPRESS]");

        var opts = Options.Create(new SemanticMemoryOptions
        {
            TraitCompressionThreshold   = traitThreshold,
            EventCompressionThreshold   = eventThreshold,
            EpisodeCompressionThreshold = episodeThreshold
        });
        return new SemanticMemoryService(
            _llmMock.Object,
            _repoMock.Object,
            _convMock.Object,
            _promptMock.Object,
            opts,
            NullLogger<SemanticMemoryService>.Instance);
    }

    // --- 헬퍼 ---

    private static ConversationContext MakeContext(
        string contextId                     = "ctx001",
        Dictionary<string, ulong>? participants  = null,
        List<ulong>? semanticMemoryRefs      = null,
        string? dynamicCacheRef              = null,
        DateTimeOffset? cacheExpiresAt       = null) => new()
    {
        Id           = contextId,
        Messages     =
        [
            new ChatMessage { Role = Role.System,    Content = "sys" },
            new ChatMessage { Role = Role.User,      Content = "persona" },
            new ChatMessage { Role = Role.User,      Content = "hello", SenderName = "Alice" },
            new ChatMessage { Role = Role.Assistant, Content = "hi" }
        ],
        Participants       = participants ?? new Dictionary<string, ulong> { ["Alice"] = 100UL },
        SemanticMemoryRefs = semanticMemoryRefs ?? new List<ulong>(),
        DynamicCacheRef    = dynamicCacheRef,
        CacheExpiresAt     = cacheExpiresAt
    };

    private static string ExtractionJson(string subject = "Alice",
        string[] traits   = null!,
        string[] events   = null!,
        string[] episodes = null!) =>
        $$"""
        {
          "users": [
            {
              "subject": "{{subject}}",
              "traits":   [{{string.Join(",", (traits   ?? []).Select(t => $"\"{t}\""))}}],
              "events":   [{{string.Join(",", (events   ?? []).Select(e => $"\"{e}\""))}}],
              "episodes": [{{string.Join(",", (episodes ?? []).Select(e => $"\"{e}\""))}}]
            }
          ]
        }
        """;

    private void SetupLlm(params string[] responses)
    {
        // 여러 호출 순차 반환
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

    private void SetupRepoEmpty()
    {
        _repoMock.Setup(r => r.GetByUserIdAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSemanticMemory?)null);
    }

    private void SetupRepoWithFacts(ulong userId, List<SemanticMemoryFact> facts)
    {
        _repoMock.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSemanticMemory { UserId = userId, Facts = facts });
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
    // 2. Subject → UserId 매핑
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_SubjectFound_AppendsFacts()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, [new SemanticMemoryFact { Content = "Alice likes cats", Type = "trait", SourceContextId = "ctx001", ExtractedAt = DateTime.UtcNow }]);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts =>
                facts.Any(f => f.Content == "Alice likes cats" && f.Type == "trait")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_SubjectNotFound_DoesNotAppendFacts()
    {
        // Participants에 없는 subject
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Bob"] = 200UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_OneSubjectMissing_OtherSubjectStillProcessed()
    {
        var json = """
            {
              "users": [
                { "subject": "Unknown", "traits": ["x"], "events": [], "episodes": [] },
                { "subject": "Alice",   "traits": ["Alice likes cats"], "events": [], "episodes": [] }
              ]
            }
            """;
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(json);
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, [new SemanticMemoryFact { Content = "Alice likes cats", Type = "trait", SourceContextId = "ctx001", ExtractedAt = DateTime.UtcNow }]);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        // Unknown은 skip, Alice는 저장
        _repoMock.Verify(r => r.AppendFactsAsync(100UL, It.IsAny<IEnumerable<SemanticMemoryFact>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 3. 성공 userId → SemanticMemoryRefs 추가 + context 저장
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_SuccessfulUser_AddsToSemanticMemoryRefs()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, [new SemanticMemoryFact { Content = "t", Type = "trait", SourceContextId = "ctx001", ExtractedAt = DateTime.UtcNow }]);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        Assert.Contains(100UL, ctx.SemanticMemoryRefs);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_SubjectNotFound_NotAddedToSemanticMemoryRefs()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Bob"] = 200UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        Assert.Empty(ctx.SemanticMemoryRefs);
    }

    // =========================================================
    // 4. 압축 임계값 (type별 독립)
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_TraitBelowThreshold_NoCompression()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        // 2개 traits — threshold=5 미달
        SetupRepoWithFacts(100UL, [
            new SemanticMemoryFact { Type = "trait", Content = "t1", SourceContextId = "x", ExtractedAt = DateTime.UtcNow },
            new SemanticMemoryFact { Type = "trait", Content = "t2", SourceContextId = "x", ExtractedAt = DateTime.UtcNow }
        ]);
        var sut = CreateSut(traitThreshold: 5);

        await sut.ExtractAndSaveAsync(ctx);

        // Append 1회, Replace 없음
        _repoMock.Verify(r => r.AppendFactsAsync(It.IsAny<ulong>(), It.IsAny<IEnumerable<SemanticMemoryFact>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repoMock.Verify(r => r.ReplaceFactsAsync(It.IsAny<ulong>(), It.IsAny<IEnumerable<SemanticMemoryFact>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_TraitThresholdReached_CompressesTrait()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        // 1st LLM call: extraction, 2nd: trait compression
        SetupLlm(
            ExtractionJson("Alice", traits: ["Alice likes cats"]),
            """["Alice is fond of cats."]"""
        );
        SetupRepoEmpty();
        // threshold=2, 2개 traits 이미 존재 → append 후 2개 → 압축 트리거
        SetupRepoWithFacts(100UL, [
            new SemanticMemoryFact { Type = "trait", Content = "t1", SourceContextId = "x", ExtractedAt = DateTime.UtcNow },
            new SemanticMemoryFact { Type = "trait", Content = "t2", SourceContextId = "x", ExtractedAt = DateTime.UtcNow }
        ]);
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.ReplaceFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts =>
                facts.Any(f => f.Type == "trait" && f.Content == "Alice is fond of cats.")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_EventThresholdReached_CompressesEventOnly()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", events: ["Alice attended event X"]),
            """["Alice went to event X."]"""
        );
        SetupRepoEmpty();
        // 2개 events, threshold=2
        SetupRepoWithFacts(100UL, [
            new SemanticMemoryFact { Type = "event", Content = "e1", SourceContextId = "x", ExtractedAt = DateTime.UtcNow },
            new SemanticMemoryFact { Type = "event", Content = "e2", SourceContextId = "x", ExtractedAt = DateTime.UtcNow }
        ]);
        var sut = CreateSut(eventThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.ReplaceFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts =>
                facts.Any(f => f.Type == "event" && f.Content == "Alice went to event X.") &&
                !facts.Any(f => f.Type == "trait")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 5. CacheHint 전달
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_ValidCache_PassesCacheHint()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var ctx = MakeContext(dynamicCacheRef: "cache123", cacheExpiresAt: expires);
        ctx.CachedMessageCount = 3;
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, []);
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
        var expires = DateTimeOffset.UtcNow.AddMinutes(-1); // 만료
        var ctx = MakeContext(dynamicCacheRef: "cache123", cacheExpiresAt: expires);
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, []);
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
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, []);
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
    // 5-b. 추가 경계 케이스
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_ZeroUsersExtracted_SavesContextAndReturns()
    {
        // LLM이 빈 users 배열 반환 → AppendFacts 없이 SaveContext 호출 후 종료
        var ctx = MakeContext();
        SetupLlm("""{"users":[]}""");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_EmptyFactsForUser_AddsToRefsWithoutAppend()
    {
        // user subject 매핑 성공 but traits/events/episodes 모두 빈 배열 → AppendFacts 없이 Refs에 추가
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice")); // traits/events/episodes 모두 null → 빈 배열
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(100UL, ctx.SemanticMemoryRefs);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_NullCacheExpiresAtWithCacheRef_NoCacheHint()
    {
        // DynamicCacheRef 있으나 CacheExpiresAt null → null hint (line 176 왼쪽 branch)
        var ctx = MakeContext(dynamicCacheRef: "cache123", cacheExpiresAt: null);
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, []);
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
    public async Task ExtractAndSaveAsync_EpisodeThresholdReached_CompressesEpisode()
    {
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", episodes: ["Alice visited Tokyo"]),
            """["Alice has been to Tokyo."]"""
        );
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, [
            new SemanticMemoryFact { Type = "episode", Content = "ep1", SourceContextId = "x", ExtractedAt = DateTime.UtcNow },
            new SemanticMemoryFact { Type = "episode", Content = "ep2", SourceContextId = "x", ExtractedAt = DateTime.UtcNow }
        ]);
        var sut = CreateSut(episodeThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.ReplaceFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts =>
                facts.Any(f => f.Type == "episode" && f.Content == "Alice has been to Tokyo.")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_CompressCheckNullMemory_CompletesNormally()
    {
        // CompressIfNeededAsync: GetByUserIdAsync null 반환 → early return, Refs 정상 추가
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(ExtractionJson("Alice", traits: ["Alice likes cats"]));
        SetupRepoEmpty(); // AppendFacts 후 GetByUserId도 null 반환
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.ReplaceFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(100UL, ctx.SemanticMemoryRefs);
    }

    // =========================================================
    // 5-c. ParseExtractionResult / ParseCompressedFacts 경계 케이스
    // =========================================================

    [Fact]
    public async Task ExtractAndSaveAsync_MarkdownFencedJson_ParsesCorrectly()
    {
        // LLM이 ```json ... ``` fence로 감싸 반환해도 정상 파싱
        var fencedJson = """
            ```json
            {"users":[{"subject":"Alice","traits":["cat lover"],"events":[],"episodes":[]}]}
            ```
            """;
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(fencedJson);
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, []);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts => facts.Any(f => f.Content == "cat lover")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_InvalidExtractionJson_SavesContextAndReturns()
    {
        // 추출 LLM이 invalid JSON 반환 → ParseExtractionResult exception catch → SaveContext 후 종료
        var ctx = MakeContext();
        SetupLlm("NOT VALID JSON {{{{");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_JsonMissingUsersProperty_ReturnsEarlyWithSave()
    {
        // LLM이 "users" 키 없는 JSON 반환 → TryGetProperty 실패 → empty list → SaveContext 후 종료
        var ctx = MakeContext();
        SetupLlm("""{"other":"value"}""");
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_UserMissingEventsField_ExtractsTraitsOnly()
    {
        // user 객체에 "events" 키 누락 → ReadStringArray missing branch → events=빈 배열, traits 정상 추출
        var partialJson = """
            {"users":[{"subject":"Alice","traits":["Alice likes cats"],"episodes":[]}]}
            """;
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(partialJson);
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, []);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts =>
                facts.Any(f => f.Type == "trait" && f.Content == "Alice likes cats") &&
                !facts.Any(f => f.Type == "event")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_CompressionMarkdownFence_ParsesCorrectly()
    {
        // 압축 LLM이 fenced JSON 반환해도 정상 파싱
        var fencedCompression = """
            ```json
            ["Alice is fond of cats."]
            ```
            """;
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", traits: ["Alice likes cats"]),
            fencedCompression
        );
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, [
            new SemanticMemoryFact { Type = "trait", Content = "t1", SourceContextId = "x", ExtractedAt = DateTime.UtcNow },
            new SemanticMemoryFact { Type = "trait", Content = "t2", SourceContextId = "x", ExtractedAt = DateTime.UtcNow }
        ]);
        var sut = CreateSut(traitThreshold: 2);

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.ReplaceFactsAsync(
            100UL,
            It.Is<IEnumerable<SemanticMemoryFact>>(facts =>
                facts.Any(f => f.Type == "trait" && f.Content == "Alice is fond of cats.")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_EmptySubjectInJson_FilteredOutLikeNoUser()
    {
        // subject가 빈 문자열 → IsNullOrWhiteSpace true → 결과에서 제외 → ZeroUsers 경로
        var emptySubjectJson = """{"users":[{"subject":"","traits":["t"],"events":[],"episodes":[]}]}""";
        var ctx = MakeContext();
        SetupLlm(emptySubjectJson);
        var sut = CreateSut();

        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.AppendFactsAsync(
            It.IsAny<ulong>(),
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _convMock.Verify(c => c.SaveContextAsync(ctx, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractAndSaveAsync_CompressionReturnsNullJson_NullCoalesced()
    {
        // 압축 LLM이 "null" 반환 → Deserialize returns null → ?? [] → empty compressed list
        var ctx = MakeContext(participants: new Dictionary<string, ulong> { ["Alice"] = 100UL });
        SetupLlm(
            ExtractionJson("Alice", traits: ["Alice likes cats"]),
            "null"  // Deserialize<List<string>>("null") returns null → ?? []
        );
        SetupRepoEmpty();
        SetupRepoWithFacts(100UL, [
            new SemanticMemoryFact { Type = "trait", Content = "t1", SourceContextId = "x", ExtractedAt = DateTime.UtcNow },
            new SemanticMemoryFact { Type = "trait", Content = "t2", SourceContextId = "x", ExtractedAt = DateTime.UtcNow }
        ]);
        var sut = CreateSut(traitThreshold: 2);

        // 압축 결과가 null → empty list → ReplaceFactsAsync 호출됨 (threshold 충족)
        await sut.ExtractAndSaveAsync(ctx);

        _repoMock.Verify(r => r.ReplaceFactsAsync(
            100UL,
            It.IsAny<IEnumerable<SemanticMemoryFact>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================
    // 6. GetFactsAsync
    // =========================================================

    [Fact]
    public async Task GetFactsAsync_NoDocument_ReturnsEmptyList()
    {
        _repoMock.Setup(r => r.GetByUserIdAsync(999UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserSemanticMemory?)null);
        var sut = CreateSut();

        var result = await sut.GetFactsAsync(999UL);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetFactsAsync_DocumentExists_ReturnsFacts()
    {
        var facts = new List<SemanticMemoryFact>
        {
            new() { Content = "Alice likes cats", Type = "trait", SourceContextId = "ctx001", ExtractedAt = DateTime.UtcNow }
        };
        _repoMock.Setup(r => r.GetByUserIdAsync(100UL, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSemanticMemory { UserId = 100UL, Facts = facts });
        var sut = CreateSut();

        var result = await sut.GetFactsAsync(100UL);

        Assert.Single(result);
        Assert.Equal("Alice likes cats", result[0].Content);
    }
}
