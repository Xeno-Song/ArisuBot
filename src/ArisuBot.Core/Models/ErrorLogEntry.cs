namespace ArisuBot.Core.Models;

/// <summary>에러 로그 분류. 어느 컴포넌트에서 발생했는지 나타낸다.</summary>
public enum ErrorType
{
    /// <summary>Gemini API 호출 실패 (ClientError / ServerError).</summary>
    LLMError,

    /// <summary>ILLMTool.ExecuteAsync 실패.</summary>
    ToolError,

    /// <summary>캐시 생성/갱신/삭제 실패.</summary>
    CacheError,

    /// <summary>그 외 예기치 않은 시스템 예외.</summary>
    SystemError
}

/// <summary>단일 에러 이벤트 데이터. IErrorLogger에 전달하는 도메인 모델.</summary>
public class ErrorLogEntry
{
    /// <summary>에러 발생 시각 (UTC).</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>에러 분류.</summary>
    public ErrorType ErrorType { get; init; }

    /// <summary>에러가 발생한 클래스 또는 컴포넌트명.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>에러 메시지.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>스택 트레이스 또는 추가 진단 정보. null 허용.</summary>
    public string? Details { get; init; }

    /// <summary>연관 세션(ConversationContext) ID. null 허용.</summary>
    public string? ContextId { get; init; }
}
