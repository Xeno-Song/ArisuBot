using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>LLM 공급자 추상화. 구현체 교체 시 호출부 변경 없음.</summary>
public interface ILLMProvider
{
    string ProviderName { get; }

    /// <summary>
    /// 메시지 목록으로 LLM 응답을 스트리밍 생성한다.
    /// tools와 toolContext가 모두 제공되면 함수 호출 루프를 실행한다.
    /// 텍스트와 FunctionCall이 동시 존재하는 iteration에서는 텍스트를 먼저 yield해 즉시 전달하고,
    /// 이후 툴을 실행한 뒤 다음 iteration의 최종 응답을 yield한다.
    /// </summary>
    /// <param name="messages">대화 히스토리 + 새 유저 메시지.</param>
    /// <param name="tools">LLM에 등록할 툴 목록. null이면 툴 비활성화.</param>
    /// <param name="toolContext">툴 실행 컨텍스트 (GuildId, ChannelId). null이면 툴 비활성화.</param>
    /// <param name="cacheHint">명시적 캐시 정보. null이면 캐시 없이 전체 컨텍스트 전송. 캐시 미지원 provider는 무시.</param>
    /// <param name="contextId">Monitor Sidecar에 전달할 대화 컨텍스트 ID (MongoDB doc ID). null이면 TOKEN_USAGE 이벤트에 contextId 없음.</param>
    /// <param name="ct">취소 토큰.</param>
    IAsyncEnumerable<LLMResponse> GenerateAsync(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<ILLMTool>? tools = null,
        LLMToolExecutionContext? toolContext = null,
        CacheHint? cacheHint = null,
        string? contextId = null,
        CancellationToken ct = default);
}
