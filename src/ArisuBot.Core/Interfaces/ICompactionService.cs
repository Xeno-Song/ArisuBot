using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>대화 컨텍스트 Compaction 실행 추상화.</summary>
public interface ICompactionService
{
    /// <summary>
    /// 컨텍스트에 Compaction을 실행한다.
    /// LLM으로 facts/summary 추출 → old session에 저장 → new session 생성 및 반환.
    /// </summary>
    Task<ConversationContext> RunAsync(ConversationContext context, CancellationToken ct = default);
}
