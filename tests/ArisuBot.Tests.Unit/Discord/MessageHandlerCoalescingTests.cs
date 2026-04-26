using ArisuBot.Core.Models;
using ArisuBot.Discord.Handlers;

namespace ArisuBot.Tests.Unit.Discord;

/// <summary>MessageHandler의 메시지 Coalescing 관련 로직 테스트.</summary>
public class MessageHandlerCoalescingTests
{
    // --- CombineBatchContent ---

    [Fact]
    public void CombineBatchContent_SingleMessage_ReturnsContentAsIs()
    {
        var batch = new List<(string Content, string DisplayName)>
        {
            ("안녕하세요", "Alice")
        };

        var result = MessageHandler.CombineBatchContent(batch, ContextType.Channel);

        Assert.Equal("안녕하세요", result);
    }

    [Fact]
    public void CombineBatchContent_Channel_MultipleMessages_FormatsWithNames()
    {
        var batch = new List<(string Content, string DisplayName)>
        {
            ("첫 번째 메시지", "Alice"),
            ("두 번째 메시지", "Bob")
        };

        var result = MessageHandler.CombineBatchContent(batch, ContextType.Channel);

        Assert.Equal("[Alice]: 첫 번째 메시지\n[Bob]: 두 번째 메시지", result);
    }

    [Fact]
    public void CombineBatchContent_Channel_SameSenderMultiple_StillFormatsWithNames()
    {
        var batch = new List<(string Content, string DisplayName)>
        {
            ("msg1", "Alice"),
            ("msg2", "Alice"),
            ("msg3", "Alice")
        };

        var result = MessageHandler.CombineBatchContent(batch, ContextType.Channel);

        Assert.Equal("[Alice]: msg1\n[Alice]: msg2\n[Alice]: msg3", result);
    }

    [Fact]
    public void CombineBatchContent_DM_MultipleMessages_JoinsWithNewlineOnly()
    {
        var batch = new List<(string Content, string DisplayName)>
        {
            ("질문1", "Alice"),
            ("질문2", "Alice")
        };

        var result = MessageHandler.CombineBatchContent(batch, ContextType.User);

        // DM은 항상 동일 발신자 — 이름 접두사 없이 단순 연결
        Assert.Equal("질문1\n질문2", result);
    }

    [Fact]
    public void CombineBatchContent_DM_SingleMessage_ReturnsContentOnly()
    {
        var batch = new List<(string Content, string DisplayName)>
        {
            ("단일 메시지", "User")
        };

        var result = MessageHandler.CombineBatchContent(batch, ContextType.User);

        Assert.Equal("단일 메시지", result);
    }

    [Fact]
    public void CombineBatchContent_Channel_ThreeMessages_AllFormatted()
    {
        var batch = new List<(string Content, string DisplayName)>
        {
            ("A", "User1"),
            ("B", "User2"),
            ("C", "User3")
        };

        var result = MessageHandler.CombineBatchContent(batch, ContextType.Channel);

        Assert.Equal("[User1]: A\n[User2]: B\n[User3]: C", result);
    }

    // --- ContextSlot 초기 상태 ---

    [Fact]
    public void ContextSlot_InitialState_IsNotProcessing()
    {
        var slot = new MessageHandler.ContextSlot();

        Assert.False(slot.IsProcessing);
    }

    [Fact]
    public void ContextSlot_InitialState_PendingCountIsZero()
    {
        var slot = new MessageHandler.ContextSlot();

        Assert.Equal(0, slot.PendingCount);
    }

    [Fact]
    public void ContextSlot_IsProcessing_CanBeSetToTrue()
    {
        var slot = new MessageHandler.ContextSlot();

        slot.IsProcessing = true;

        Assert.True(slot.IsProcessing);
    }

    [Fact]
    public void ContextSlot_IsProcessing_CanBeReset()
    {
        var slot = new MessageHandler.ContextSlot();
        slot.IsProcessing = true;

        slot.IsProcessing = false;

        Assert.False(slot.IsProcessing);
    }
}
