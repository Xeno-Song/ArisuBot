using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Options;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

public class GeminiCacheManagerTests
{
    private readonly Mock<IGeminiCacheClient> _cacheMock = new();
    private readonly Mock<ILlmMonitorServer> _pipeMock = new();
    private readonly GeminiCacheManager _sut;

    // 테스트 기준값 — 실제 appsettings 독립적으로 제어
    // InitialThresholdTokens < RefreshThresholdTokens — 두 임계값 간 경계 케이스 검증 가능
    private const int InitialThresholdTokens = 500;
    private const int RefreshThresholdTokens = 1000;
    private const int VelocityWindowSeconds  = 180;
    private const int VelocityMinMessages    = 3;

    public GeminiCacheManagerTests()
    {
        var cacheOpts = Options.Create(new CacheOptions
        {
            Enabled                = true,
            InitialThresholdTokens = InitialThresholdTokens,
            RefreshThresholdTokens = RefreshThresholdTokens,
            TtlSeconds             = 900,
            VelocityWindowSeconds  = VelocityWindowSeconds,
            VelocityMinMessages    = VelocityMinMessages
        });
        var geminiOpts = Options.Create(new GeminiOptions { Model = "gemini-test", ApiKey = "key" });
        _sut = new GeminiCacheManager(
            _cacheMock.Object,
            _pipeMock.Object,
            cacheOpts,
            geminiOpts,
            new Mock<ILogger<GeminiCacheManager>>().Object);
    }

    // --- 헬퍼 ---

    private static ConversationContext MakeContext(
        int uncachedTokens      = 0,
        string? dynamicCacheRef = null,
        int cachedMessageCount  = 0,
        List<DateTimeOffset>? timestamps = null) => new()
    {
        DynamicCacheRef         = dynamicCacheRef,
        CachedMessageCount      = cachedMessageCount,
        UncachedTokenCount      = uncachedTokens,
        RecentMessageTimestamps = timestamps ?? []
    };

    /// <summary>System 1개 + User/Assistant 교대 n회 + 새 User 메시지 1개로 구성된 메시지 목록.</summary>
    private static List<ChatMessage> MakeMessages(int exchangeCount = 2)
    {
        var msgs = new List<ChatMessage>
        {
            new() { Role = Role.System, Content = "system prompt" }
        };
        for (var i = 0; i < exchangeCount; i++)
        {
            msgs.Add(new() { Role = Role.User,      Content = $"user {i}" });
            msgs.Add(new() { Role = Role.Assistant, Content = $"assistant {i}" });
        }
        msgs.Add(new() { Role = Role.User, Content = "new message" });
        return msgs;
    }

    private static CachedContent MakeCachedContent(string name) => new() { Name = name };

    /// <summary>velocity window 내에 VelocityMinMessages 개의 타임스탬프를 생성한다.</summary>
    private static List<DateTimeOffset> VelocityTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(1, VelocityMinMessages)
            .Select(i => now.AddSeconds(-i * 10)) // 10초 간격 — 모두 window 안
            .ToList();
    }

    // =========================================================================
    // Enabled 플래그
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_ReturnsNull_WhenDisabled()
    {
        var sut = new GeminiCacheManager(
            _cacheMock.Object,
            new Mock<ILlmMonitorServer>().Object,
            Options.Create(new CacheOptions { Enabled = false }),
            Options.Create(new GeminiOptions { Model = "m", ApiKey = "k" }),
            new Mock<ILogger<GeminiCacheManager>>().Object);

        var result = await sut.TryRollCacheAsync(MakeContext(), MakeMessages(), null);

        Assert.Null(result);
        _cacheMock.VerifyNoOtherCalls();
    }

    // =========================================================================
    // 초기 임계값 미달 (cache 없음)
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_ReturnsNull_WhenInitialThresholdNotMetAndNoCacheExists()
    {
        var context = MakeContext(uncachedTokens: InitialThresholdTokens - 1);

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.Null(result);
        _cacheMock.VerifyNoOtherCalls();
    }

    // =========================================================================
    // refresh 임계값 미달 (cache 있음)
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_ReturnsExistingHint_WhenRefreshThresholdNotMetAndCacheExists()
    {
        // uncachedTokens가 InitialThreshold 초과 but RefreshThreshold 미달 — 기존 cache 재사용
        var context = MakeContext(
            uncachedTokens:     RefreshThresholdTokens - 1,
            dynamicCacheRef:    "caches/existing",
            cachedMessageCount: 4);

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/existing", result!.CachedContentName);
        Assert.Equal(4, result.CachedMessageCount);
        _cacheMock.VerifyNoOtherCalls();
    }

    // =========================================================================
    // 두 임계값 사이 — 경계 케이스
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_CreatesInitialCache_WhenBetweenInitialAndRefreshThreshold_NoCacheExists()
    {
        // InitialThreshold 초과 but RefreshThreshold 미달 + cache 없음 → 최초 생성 (velocity 충족 시)
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1, // InitialThreshold < this < RefreshThreshold
            timestamps:     VelocityTimestamps());

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCachedContent("caches/initial"));

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/initial", result!.CachedContentName);
        _cacheMock.Verify(c => c.CreateAsync(
            It.IsAny<string>(), It.IsAny<Content?>(),
            It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryRollCacheAsync_ReusesExistingCache_WhenBetweenInitialAndRefreshThreshold_CacheExists()
    {
        // InitialThreshold 초과 but RefreshThreshold 미달 + cache 있음 → rolling 하지 않고 재사용
        var context = MakeContext(
            uncachedTokens:     InitialThresholdTokens + 1, // InitialThreshold < this < RefreshThreshold
            dynamicCacheRef:    "caches/existing",
            cachedMessageCount: 4);

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/existing", result!.CachedContentName);
        Assert.Equal(4, result.CachedMessageCount);
        _cacheMock.VerifyNoOtherCalls(); // rolling 없음
    }

    // =========================================================================
    // 초기 캐시 생성 — velocity gate 적용
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_ReturnsNull_WhenInitialThresholdMetButVelocityNotMet()
    {
        // window 내 타임스탬프 1개 — VelocityMinMessages(3) 미충족
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1,
            timestamps:     [DateTimeOffset.UtcNow.AddSeconds(-10)]);

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.Null(result);
        _cacheMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TryRollCacheAsync_CreatesCache_WhenInitialThresholdAndVelocityMet()
    {
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1,
            timestamps:     VelocityTimestamps());

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCachedContent("caches/new-cache"));

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/new-cache", result!.CachedContentName);
        // context 갱신 확인
        Assert.Equal("caches/new-cache", context.DynamicCacheRef);
        Assert.Equal(0, context.UncachedTokenCount);
        _cacheMock.Verify(c => c.CreateAsync(
            It.IsAny<string>(), It.IsAny<Content?>(),
            It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // refresh 임계값 도달 — 기존 캐시 갱신 (rolling, velocity gate 없음)
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_RollsCache_WhenRefreshThresholdMetAndCacheExists()
    {
        // 기존 캐시 존재 시 velocity gate 없이 바로 생성/교체
        var context = MakeContext(
            uncachedTokens:     RefreshThresholdTokens + 1,
            dynamicCacheRef:    "caches/old",
            cachedMessageCount: 2);

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCachedContent("caches/new-cache"));
        _cacheMock.Setup(c => c.DeleteAsync("caches/old", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/new-cache", result!.CachedContentName);
        // 이전 캐시 삭제 호출 검증
        _cacheMock.Verify(c => c.DeleteAsync("caches/old", It.IsAny<CancellationToken>()), Times.Once);
        // context 갱신
        Assert.Equal("caches/new-cache", context.DynamicCacheRef);
        Assert.Equal(0, context.UncachedTokenCount);
    }

    [Fact]
    public async Task TryRollCacheAsync_OldCacheDeleteFails_StillReturnsNewHint()
    {
        // 이전 캐시 삭제 실패(이미 만료)해도 새 캐시 hint 반환
        var context = MakeContext(
            uncachedTokens:     RefreshThresholdTokens + 1,
            dynamicCacheRef:    "caches/old",
            cachedMessageCount: 2);

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCachedContent("caches/new-cache"));
        _cacheMock.Setup(c => c.DeleteAsync("caches/old", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("already expired"));

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/new-cache", result!.CachedContentName);
    }

    // =========================================================================
    // 캐시 생성 실패
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_ReturnsNull_WhenCreateFailsAndNoCacheExists()
    {
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1,
            timestamps:     VelocityTimestamps());

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API error"));

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRollCacheAsync_ReturnsOldHint_WhenCreateFailsAndCacheExists()
    {
        var context = MakeContext(
            uncachedTokens:     RefreshThresholdTokens + 1,
            dynamicCacheRef:    "caches/old",
            cachedMessageCount: 2);

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API error"));

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        Assert.NotNull(result);
        Assert.Equal("caches/old", result!.CachedContentName);
        Assert.Equal(2, result.CachedMessageCount);
    }

    // =========================================================================
    // Timestamp pruning
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_PrunesExpiredTimestamps_BeforeVelocityCheck()
    {
        // 3개 타임스탬프 중 2개는 window 밖 → prune 후 1개만 남아 velocity 미충족
        var now = DateTimeOffset.UtcNow;
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1,
            timestamps:
            [
                now.AddSeconds(-10),                          // window 안
                now.AddSeconds(-(VelocityWindowSeconds + 10)), // window 밖
                now.AddSeconds(-(VelocityWindowSeconds + 60))  // window 밖
            ]);

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(), null);

        // velocity 미충족 → null
        Assert.Null(result);
        // window 밖 항목 pruned
        Assert.Single(context.RecentMessageTimestamps);
        _cacheMock.VerifyNoOtherCalls();
    }

    // =========================================================================
    // CachedMessageCount 계산
    // =========================================================================

    [Fact]
    public async Task TryRollCacheAsync_CachedMessageCount_IsContentCount()
    {
        // CachedMessageCount = merged Content 수 (alternating history → message 수 = Content 수)
        // messages: System + [User+Asst]*2 + newUser
        // historyToCache = [User0, Asst0, User1, Asst1] → alternating → 4 Contents
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1,
            timestamps:     VelocityTimestamps());

        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCachedContent("caches/x"));

        var result = await _sut.TryRollCacheAsync(context, MakeMessages(exchangeCount: 2), null);

        var historyMsgs = MakeMessages(exchangeCount: 2).Where(m => m.Role != Role.System).SkipLast(1).ToList();
        var expectedContentCount = GeminiContentBuilder.BuildContents(historyMsgs).Count;
        Assert.Equal(expectedContentCount, result!.CachedMessageCount);
    }

    [Fact]
    public async Task TryRollCacheAsync_TrimsTrailingUserMessages_BeforeCreatingCache()
    {
        // historyToCache가 User로 끝나면 model role로 trim — cache 경계 clean
        // messages: System + User0 + Asst0 + User1(trailing, trimmed) + newUser
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.System,    Content = "system" },
            new() { Role = Role.User,      Content = "user0" },
            new() { Role = Role.Assistant, Content = "asst0" },
            new() { Role = Role.User,      Content = "user1-trailing" }, // trim 대상
            new() { Role = Role.User,      Content = "new-message" }     // 새 메시지 (항상 제외)
        };
        var context = MakeContext(
            uncachedTokens: InitialThresholdTokens + 1,
            timestamps:     VelocityTimestamps());

        IEnumerable<Content>? capturedContents = null;
        _cacheMock.Setup(c => c.CreateAsync(
                It.IsAny<string>(), It.IsAny<Content?>(),
                It.IsAny<IEnumerable<Tool>?>(), It.IsAny<IEnumerable<Content>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Content?, IEnumerable<Tool>?, IEnumerable<Content>, string, CancellationToken>(
                (_, _, _, contents, _, _) => capturedContents = contents)
            .ReturnsAsync(MakeCachedContent("caches/x"));

        await _sut.TryRollCacheAsync(context, messages, null);

        Assert.NotNull(capturedContents);
        var contentList = capturedContents!.ToList();
        // 마지막 Content는 model role(Asst0) — user1-trailing이 trim됨
        Assert.Equal("model", contentList[^1].Role);
    }

    [Fact]
    public async Task TryRollCacheAsync_ReturnsNull_WhenHistoryEmptyAfterTrim()
    {
        // 이력이 User만 있어 trim 후 empty → 기존 캐시 없으면 null
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.System, Content = "system" },
            new() { Role = Role.User,   Content = "user0" }, // trim 후 empty
            new() { Role = Role.User,   Content = "new-message" }
        };
        var context = MakeContext(uncachedTokens: InitialThresholdTokens + 1, timestamps: VelocityTimestamps());

        var result = await _sut.TryRollCacheAsync(context, messages, null);

        Assert.Null(result);
        _cacheMock.VerifyNoOtherCalls();
    }
}
