namespace ArisuBot.Core.Models;

/// <summary>
/// Semantic memory 데이터 단위. extracted(세션 단위 추출분)와 snapshot(누적분) 양쪽에 사용.
/// </summary>
public class SemanticMemoryData
{
    /// <summary>영속적 성향·선호·습관 목록.</summary>
    public List<string> Traits { get; set; } = new();

    /// <summary>경험·사건·에피소드 목록.</summary>
    public List<string> Episodic { get; set; } = new();
}

/// <summary>semantic memory document의 state 상수.</summary>
public static class SemanticMemoryState
{
    public const string Active   = "active";
    public const string Inactive = "inactive";
}
