using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>LLM이 호출할 수 있는 툴의 추상화. 각 구현체는 특정 Discord 작업을 수행한다.</summary>
public interface ILLMTool
{
    /// <summary>툴 이름. LLMToolDefinition.Name과 일치해야 한다.</summary>
    string Name { get; }

    /// <summary>LLM에 등록할 툴 정의 (이름, 설명, 파라미터 스키마).</summary>
    LLMToolDefinition Definition { get; }

    /// <summary>
    /// LLM의 함수 호출 요청을 실제로 실행한다.
    ///
    /// 예외 처리 계약:
    /// - 예측 가능한 실패(파라미터 오류, Discord 권한 부족, 대상 미존재 등)는
    ///   try-catch로 처리하고 <see cref="ToolResult.Fail"/>을 반환할 것.
    /// - 처리되지 않은 예외는 GeminiProvider가 최후 안전망으로 잡아 ToolResult.Fail로 변환함.
    ///   단, <see cref="OperationCanceledException"/>은 GeminiProvider가 re-throw해 iteration을 중단함.
    /// </summary>
    /// <param name="arguments">LLM이 전달한 파라미터. FunctionCall.Args와 동일한 타입.</param>
    /// <param name="context">실행 컨텍스트 (GuildId, ChannelId).</param>
    /// <param name="ct">취소 토큰.</param>
    /// <returns>실행 결과. JSON 직렬화 후 Gemini FunctionResponse에 포함된다.</returns>
    Task<ToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object> arguments,
        LLMToolExecutionContext context,
        CancellationToken ct = default);
}
