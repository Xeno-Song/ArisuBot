using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>
/// 유저별 session semantic memory 저장소.
/// document는 per-session 생성. state("active"/"inactive")는 수동 관리.
/// </summary>
public interface ISemanticMemoryRepository
{
    /// <summary>
    /// userId의 가장 최신 active document 반환. 없으면 null.
    /// LLM 주입 및 신규 snapshot 누적 시 기준점으로 사용.
    /// </summary>
    Task<UserSemanticMemory?> GetLatestActiveAsync(ulong userId, CancellationToken ct = default);

    /// <summary>신규 session memory document 삽입. Id는 MongoDB가 생성.</summary>
    Task CreateAsync(UserSemanticMemory memory, CancellationToken ct = default);

    /// <summary>지정 document의 state를 변경한다 (수동 rollback/비활성화 용도).</summary>
    Task SetStateAsync(string id, string state, CancellationToken ct = default);
}
