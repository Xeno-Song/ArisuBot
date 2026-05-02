using ArisuBot.Core.Models;
using ArisuBot.Discord.Handlers;

namespace ArisuBot.Tests.Unit.Discord;

/// <summary>MessageHandler.BuildSemanticMemoryBlock 주입 포맷 테스트.</summary>
public class SemanticMemoryInjectionTests
{
    private static SemanticMemoryFact MakeFact(string content, string type) =>
        new() { Content = content, Type = type, SourceContextId = "ctx", ExtractedAt = DateTime.UtcNow };

    // =========================================================
    // 1. 단일 유저 — [Username] 태그 포함 확인
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_SingleUser_IncludesNameTag()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>
        {
            ("Alice", new[] { MakeFact("Alice likes cats", "trait") })
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        Assert.Contains("[Alice]", block);
        Assert.Contains("<semantic_memory>", block);
        Assert.Contains("</semantic_memory>", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_SingleUser_IncludesFactWithTypePrefix()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>
        {
            ("Alice", new[] { MakeFact("Alice likes cats", "trait") })
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        Assert.Contains("[trait] Alice likes cats", block);
    }

    // =========================================================
    // 2. 복수 유저 — 모든 섹션 포함
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_MultipleUsers_IncludesAllNameTags()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>
        {
            ("Alice", new[] { MakeFact("Alice likes cats", "trait") }),
            ("Bob",   new[] { MakeFact("Bob attended event X", "event") })
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        Assert.Contains("[Alice]", block);
        Assert.Contains("[Bob]", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_MultipleUsers_FactsAreUnderCorrectUser()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>
        {
            ("Alice", new[] { MakeFact("Alice likes cats", "trait") }),
            ("Bob",   new[] { MakeFact("Bob attended event X", "event") })
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        // Alice 섹션 이후 Bob 섹션 순서
        var aliceIdx = block.IndexOf("[Alice]", StringComparison.Ordinal);
        var bobIdx   = block.IndexOf("[Bob]",   StringComparison.Ordinal);
        var aliceFact = block.IndexOf("Alice likes cats", StringComparison.Ordinal);
        var bobFact   = block.IndexOf("Bob attended event X", StringComparison.Ordinal);

        Assert.True(aliceIdx < aliceFact);
        Assert.True(bobIdx   < bobFact);
        Assert.True(aliceIdx < bobIdx);
    }

    // =========================================================
    // 3. facts 없는 유저 → null 반환 (주입 skip 신호)
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_NoFacts_ReturnsNull()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>();

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        Assert.Null(block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_AllUsersEmptyFacts_ReturnsNull()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>
        {
            ("Alice", Array.Empty<SemanticMemoryFact>())
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        Assert.Null(block);
    }

    // =========================================================
    // 4. 타입별 복수 facts — 모두 포함
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_MixedTypes_AllIncluded()
    {
        var userMemories = new List<(string Name, IReadOnlyList<SemanticMemoryFact> Facts)>
        {
            ("Alice", new[]
            {
                MakeFact("Alice likes cats",         "trait"),
                MakeFact("Alice attended event X",   "event"),
                MakeFact("Alice told about burnout", "episode")
            })
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(userMemories);

        Assert.Contains("[trait] Alice likes cats",         block);
        Assert.Contains("[event] Alice attended event X",   block);
        Assert.Contains("[episode] Alice told about burnout", block);
    }
}
