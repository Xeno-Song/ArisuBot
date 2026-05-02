namespace ArisuBot.Core.Models;

/// <summary>특정 Discord 유저의 semantic memory. SemanticMemoryFact 목록을 보유한다.</summary>
public class UserSemanticMemory
{
    /// <summary>Discord User ID.</summary>
    public ulong UserId { get; set; }

    /// <summary>누적된 fact 목록 (trait + event + episode 혼합).</summary>
    public List<SemanticMemoryFact> Facts { get; set; } = new();

    /// <summary>마지막 갱신 시각 (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
