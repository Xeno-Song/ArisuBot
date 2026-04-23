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
}
