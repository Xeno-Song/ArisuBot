using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>유저별 session semantic memory 추출, 저장, 압축, 조회 서비스 추상화.</summary>
public interface ISemanticMemoryService
{
    /// <summary>
    /// context.Messages 전체를 LLM에 제공해 유저별 semantic memory를 추출하고 저장한다.
    /// context.SemanticMemoryRefs.Any() == true이면 중복 방지를 위해 즉시 반환.
    /// 각 유저에 대해 이전 최신 active snapshot을 조회하고, 추출분을 누적해 신규 document를 생성한다.
    /// snapshot trait/episodic이 threshold 초과 시 압축 후 저장.
    /// subject 조회 실패 시 LogError 후 해당 유저 skip.
    /// 저장 성공한 userId를 context.SemanticMemoryRefs에 append 후 context를 저장한다.
    /// 예외는 호출 측에 전파 — 격리는 호출 측 책임.
    /// </summary>
    Task ExtractAndSaveAsync(ConversationContext context, CancellationToken ct = default);

    /// <summary>
    /// userId의 가장 최신 active snapshot 반환. 없으면 null.
    /// LLM 주입 시 호출.
    /// </summary>
    Task<SemanticMemoryData?> GetLatestSnapshotAsync(ulong userId, CancellationToken ct = default);
}
