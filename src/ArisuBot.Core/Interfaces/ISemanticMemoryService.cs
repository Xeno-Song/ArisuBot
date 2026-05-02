using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>유저별 semantic memory 추출, 저장, 압축, 조회 서비스 추상화.</summary>
public interface ISemanticMemoryService
{
    /// <summary>
    /// compactedContext.Messages 전체를 LLM에 제공해 유저별 semantic memory를 추출하고 저장한다.
    /// compactedContext.SemanticMemoryRefs.Any() == true이면 중복 방지를 위해 즉시 반환.
    /// LLM 출력의 subject를 compactedContext.Participants 딕셔너리로 조회해 userId를 획득한다.
    /// subject 조회 실패 시 LogError 후 해당 유저 skip.
    /// 저장 성공한 userId를 compactedContext.SemanticMemoryRefs에 append 후 context를 저장한다.
    /// 예외는 호출 측에 전파 — 격리는 호출 측 책임.
    /// </summary>
    Task ExtractAndSaveAsync(ConversationContext compactedContext, CancellationToken ct = default);

    /// <summary>userId의 semantic memory fact 목록 반환. 없으면 빈 리스트.</summary>
    Task<IReadOnlyList<SemanticMemoryFact>> GetFactsAsync(ulong userId, CancellationToken ct = default);
}
