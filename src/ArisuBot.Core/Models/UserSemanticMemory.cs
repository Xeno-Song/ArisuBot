namespace ArisuBot.Core.Models;

/// <summary>
/// 유저의 session별 semantic memory 도메인 모델.
/// 각 session 종료 시 신규 document 생성. snapshot은 이전 누적분 + 이번 extracted.
/// state = "active" | "inactive" (수동 관리). LLM 주입 시 최신 active의 snapshot 사용.
/// </summary>
public class UserSemanticMemory
{
    /// <summary>MongoDB ObjectId (문자열). 신규 생성 시 빈 문자열 — InsertOne이 채운다.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Discord User ID.</summary>
    public ulong UserId { get; set; }

    /// <summary>이 memory를 추출한 conversation session ID.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>"active" 또는 "inactive". 수동으로만 변경.</summary>
    public string State { get; set; } = SemanticMemoryState.Active;

    /// <summary>이번 session에서 새로 추출된 데이터 (rollback 기준점).</summary>
    public SemanticMemoryData Extracted { get; set; } = new();

    /// <summary>이전 최신 snapshot + Extracted 누적 결과. LLM 주입에 사용.</summary>
    public SemanticMemoryData Snapshot { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
