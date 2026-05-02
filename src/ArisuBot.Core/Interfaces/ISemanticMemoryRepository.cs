using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>유저별 semantic memory 영속화 추상화.</summary>
public interface ISemanticMemoryRepository
{
    /// <summary>userId에 해당하는 semantic memory 반환. 없으면 null.</summary>
    Task<UserSemanticMemory?> GetByUserIdAsync(ulong userId, CancellationToken ct = default);

    /// <summary>
    /// facts를 userId 도큐먼트에 append upsert.
    /// 도큐먼트 없으면 신규 생성.
    /// </summary>
    Task AppendFactsAsync(ulong userId, IEnumerable<SemanticMemoryFact> facts, CancellationToken ct = default);

    /// <summary>userId 도큐먼트의 facts 배열 전체를 교체한다. 압축 완료 후 호출.</summary>
    Task ReplaceFactsAsync(ulong userId, IEnumerable<SemanticMemoryFact> facts, CancellationToken ct = default);
}
