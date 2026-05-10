using ArisuBot.Core.Models;
using ArisuBot.Discord.Handlers;

namespace ArisuBot.Tests.Unit.Discord;

/// <summary>MessageHandler.BuildSemanticMemoryBlock 주입 포맷 테스트.</summary>
public class SemanticMemoryInjectionTests
{
    private static SemanticMemoryData MakeSnapshot(
        string[]? traits   = null,
        string[]? episodic = null) => new()
    {
        Traits   = traits   is not null ? new List<string>(traits)   : new(),
        Episodic = episodic is not null ? new List<string>(episodic) : new()
    };

    // =========================================================
    // 1. 단일 유저 — 태그 및 구조 확인
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_SingleUser_IncludesNameTag()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(traits: ["Alice likes cats"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Contains("[Alice]", block);
        Assert.Contains("<semantic_memory>", block);
        Assert.Contains("</semantic_memory>", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_SingleUser_TraitsUnderTraitsSection()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(traits: ["Alice likes cats"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Contains("[Traits]", block);
        Assert.Contains("- Alice likes cats", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_SingleUser_EpisodicUnderEpisodicSection()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(episodic: ["Alice visited Tokyo"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Contains("[Episodic]", block);
        Assert.Contains("- Alice visited Tokyo", block);
    }

    // =========================================================
    // 2. 복수 유저 — 모든 섹션 포함
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_MultipleUsers_IncludesAllNameTags()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(traits: ["Alice likes cats"])),
            ("Bob",   MakeSnapshot(traits: ["Bob likes pizza"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Contains("[Alice]", block);
        Assert.Contains("[Bob]", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_MultipleUsers_FactsAreUnderCorrectUser()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(traits: ["Alice likes cats"])),
            ("Bob",   MakeSnapshot(traits: ["Bob likes pizza"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        // Alice 섹션 이후 Bob 섹션 순서
        var aliceIdx  = block!.IndexOf("[Alice]",        StringComparison.Ordinal);
        var bobIdx    = block.IndexOf("[Bob]",           StringComparison.Ordinal);
        var aliceFact = block.IndexOf("Alice likes cats", StringComparison.Ordinal);
        var bobFact   = block.IndexOf("Bob likes pizza",  StringComparison.Ordinal);

        Assert.True(aliceIdx < aliceFact);
        Assert.True(bobIdx   < bobFact);
        Assert.True(aliceIdx < bobIdx);
    }

    // =========================================================
    // 3. null / 빈 snapshot → null 반환 (주입 skip 신호)
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_EmptyList_ReturnsNull()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>();

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Null(block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_NullSnapshot_ReturnsNull()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", null)
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Null(block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_AllUsersEmptySnapshot_ReturnsNull()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot()) // traits/episodic 모두 빈 배열
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Null(block);
    }

    // =========================================================
    // 4. traits + episodic 혼합 — 섹션 분리
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_TraitsAndEpisodic_BothSectionsPresent()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(
                traits:   ["Alice likes cats"],
                episodic: ["Alice visited Tokyo"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Contains("[Traits]",   block);
        Assert.Contains("[Episodic]", block);
        Assert.Contains("- Alice likes cats",    block);
        Assert.Contains("- Alice visited Tokyo", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_TraitsAndEpisodic_TraitsSectionBeforeEpisodicSection()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(
                traits:   ["Alice likes cats"],
                episodic: ["Alice visited Tokyo"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        var traitsIdx   = block!.IndexOf("[Traits]",   StringComparison.Ordinal);
        var episodicIdx = block.IndexOf("[Episodic]", StringComparison.Ordinal);

        Assert.True(traitsIdx < episodicIdx);
    }

    // =========================================================
    // 5. 섹션 선택적 출력 — 한쪽만 있을 때
    // =========================================================

    [Fact]
    public void BuildSemanticMemoryBlock_OnlyTraits_NoEpisodicSection()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(traits: ["Alice likes cats"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.Contains("[Traits]",   block);
        Assert.DoesNotContain("[Episodic]", block);
    }

    [Fact]
    public void BuildSemanticMemoryBlock_OnlyEpisodic_NoTraitsSection()
    {
        var memories = new List<(string Name, SemanticMemoryData? Snapshot)>
        {
            ("Alice", MakeSnapshot(episodic: ["Alice visited Tokyo"]))
        };

        var block = MessageHandler.BuildSemanticMemoryBlock(memories);

        Assert.DoesNotContain("[Traits]", block);
        Assert.Contains("[Episodic]",     block);
    }
}
