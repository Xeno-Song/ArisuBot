using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using EphemeralMongo;
using Microsoft.Extensions.Options;

namespace ArisuBot.Tests.Integration.MongoDB;

public class ConversationRepositoryTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private ConversationRepository _sut = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync();
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = _runner.ConnectionString,
            DatabaseName = "test_db"
        });
        _sut = new ConversationRepository(new MongoDbContext(options));
    }

    public Task DisposeAsync()
    {
        _runner.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetUserContextAsync_ReturnsEmptyContext_WhenNotFound()
    {
        var context = await _sut.GetUserContextAsync(1UL);

        Assert.Equal(1UL, context.TargetId);
        Assert.Equal(ContextType.User, context.Type);
        Assert.Empty(context.Messages);
    }

    [Fact]
    public async Task GetChannelContextAsync_ReturnsEmptyContext_WhenNotFound()
    {
        var context = await _sut.GetChannelContextAsync(2UL);

        Assert.Equal(2UL, context.TargetId);
        Assert.Equal(ContextType.Channel, context.Type);
        Assert.Empty(context.Messages);
    }

    [Fact]
    public async Task SaveContextAsync_PersistsAndRetrievesContext()
    {
        var context = new ConversationContext
        {
            TargetId = 100UL,
            Type = ContextType.User,
            Messages = [new() { Role = Role.User, Content = "hello" }]
        };

        await _sut.SaveContextAsync(context);
        var retrieved = await _sut.GetUserContextAsync(100UL);

        Assert.Single(retrieved.Messages);
        Assert.Equal("hello", retrieved.Messages[0].Content);
    }

    [Fact]
    public async Task SaveContextAsync_UpdatesExistingContext()
    {
        var context = new ConversationContext { TargetId = 200UL, Type = ContextType.User };
        context.Messages.Add(new() { Role = Role.User, Content = "first" });
        await _sut.SaveContextAsync(context);

        context.Messages.Add(new() { Role = Role.Assistant, Content = "second" });
        await _sut.SaveContextAsync(context);

        var retrieved = await _sut.GetUserContextAsync(200UL);
        Assert.Equal(2, retrieved.Messages.Count);
    }

    [Fact]
    public async Task CreateNewSessionAsync_ReturnsNewEmptyContext_WithSameTargetId()
    {
        // 기존 세션 생성 및 메시지 추가
        var existing = new ConversationContext
        {
            TargetId = 300UL,
            Type = ContextType.Channel,
            Messages = [new() { Role = Role.User, Content = "old message" }]
        };
        await _sut.SaveContextAsync(existing);

        // 새 세션 생성
        var newSession = await _sut.CreateNewSessionAsync(300UL, ContextType.Channel);

        Assert.Equal(300UL, newSession.TargetId);
        Assert.Equal(ContextType.Channel, newSession.Type);
        Assert.Empty(newSession.Messages);
        Assert.NotEqual(existing.Id, newSession.Id);
    }

    [Fact]
    public async Task GetChannelContextAsync_ReturnsLatestSession_AfterCreateNewSession()
    {
        // 기존 세션 생성
        var existing = new ConversationContext
        {
            TargetId = 400UL,
            Type = ContextType.Channel,
            Messages = [new() { Role = Role.User, Content = "old" }]
        };
        await _sut.SaveContextAsync(existing);

        // 새 세션 생성 (시간 차이를 확보하기 위해 CreatedAt 조작)
        await Task.Delay(10);
        await _sut.CreateNewSessionAsync(400UL, ContextType.Channel);

        // GetChannelContextAsync는 최신 세션(빈 메시지)을 반환해야 함
        var retrieved = await _sut.GetChannelContextAsync(400UL);

        Assert.Empty(retrieved.Messages);
    }

    [Fact]
    public async Task CreateNewSessionAsync_PreservesOldDocument()
    {
        // 기존 세션 저장
        var context = new ConversationContext
        {
            TargetId = 500UL,
            Type = ContextType.User,
            Messages = [new() { Role = Role.User, Content = "history" }]
        };
        await _sut.SaveContextAsync(context);
        var oldId = context.Id;

        await Task.Delay(10);
        var newSession = await _sut.CreateNewSessionAsync(500UL, ContextType.User);

        // 새 세션 Id는 달라야 함 — 기존 doc 삭제하지 않음
        Assert.NotEqual(oldId, newSession.Id);
        Assert.NotEmpty(oldId);
        Assert.NotEmpty(newSession.Id);
    }

    [Fact]
    public async Task SaveContextAsync_PersistsParticipants()
    {
        var context = new ConversationContext
        {
            TargetId = 600UL,
            Type = ContextType.Channel,
            Participants = new Dictionary<string, ulong> { ["Xeno"] = 123456789UL }
        };

        await _sut.SaveContextAsync(context);
        var retrieved = await _sut.GetChannelContextAsync(600UL);

        Assert.Single(retrieved.Participants);
        Assert.Equal(123456789UL, retrieved.Participants["Xeno"]);
    }

    [Fact]
    public async Task SaveContextAsync_PersistsSenderName_InMessages()
    {
        var context = new ConversationContext
        {
            TargetId = 700UL,
            Type = ContextType.User,
            Messages = [new() { Role = Role.User, Content = "hello", SenderName = "Xeno" }]
        };

        await _sut.SaveContextAsync(context);
        var retrieved = await _sut.GetUserContextAsync(700UL);

        Assert.Single(retrieved.Messages);
        Assert.Equal("Xeno", retrieved.Messages[0].SenderName);
    }

    [Fact]
    public async Task ClearAllDynamicCacheRefsAsync_RestoresUncachedTokenCount_FromLastTotalTokens()
    {
        // 캐시 ref + LastTotalTokens가 있는 컨텍스트 저장
        var context = new ConversationContext
        {
            TargetId = 800UL,
            Type = ContextType.User,
            DynamicCacheRef    = "caches/some-cache",
            CachedMessageCount = 4,
            UncachedTokenCount = 0,     // 캐시 생성 직후 상태 — 0
            LastTotalTokens    = 85000  // 이전 응답에서 측정된 실제 context 규모
        };
        await _sut.SaveContextAsync(context);

        await _sut.ClearAllDynamicCacheRefsAsync();

        var retrieved = await _sut.GetUserContextAsync(800UL);
        // DynamicCacheRef 제거, CachedMessageCount 초기화
        Assert.Null(retrieved.DynamicCacheRef);
        Assert.Equal(0, retrieved.CachedMessageCount);
        // UncachedTokenCount = LastTotalTokens — 0이 아님
        Assert.Equal(85000, retrieved.UncachedTokenCount);
        // LastTotalTokens 자체는 유지
        Assert.Equal(85000, retrieved.LastTotalTokens);
    }

    [Fact]
    public async Task ClearAllDynamicCacheRefsAsync_SetsUncachedTokenCountToZero_WhenLastTotalTokensIsZero()
    {
        // LastTotalTokens가 0인 경우 (신규 세션 또는 첫 응답 전 재시작)
        var context = new ConversationContext
        {
            TargetId = 801UL,
            Type = ContextType.User,
            DynamicCacheRef    = "caches/some-cache",
            CachedMessageCount = 2,
            UncachedTokenCount = 0,
            LastTotalTokens    = 0
        };
        await _sut.SaveContextAsync(context);

        await _sut.ClearAllDynamicCacheRefsAsync();

        var retrieved = await _sut.GetUserContextAsync(801UL);
        Assert.Null(retrieved.DynamicCacheRef);
        Assert.Equal(0, retrieved.UncachedTokenCount); // LastTotalTokens = 0이므로 0 유지
    }

    [Fact]
    public async Task ClearAllDynamicCacheRefsAsync_DoesNotAffectContextsWithoutCacheRef()
    {
        // dynamicCacheRef 없는 컨텍스트는 영향 없음
        var context = new ConversationContext
        {
            TargetId = 802UL,
            Type = ContextType.User,
            UncachedTokenCount = 12000,
            LastTotalTokens    = 50000
        };
        await _sut.SaveContextAsync(context);

        await _sut.ClearAllDynamicCacheRefsAsync();

        var retrieved = await _sut.GetUserContextAsync(802UL);
        // 변경 없음
        Assert.Equal(12000, retrieved.UncachedTokenCount);
    }
}
