namespace ArisuBot.Core.Models;

/// <summary>단일 대화 메시지.</summary>
public record ChatMessage
{
    public Role Role { get; init; }
    public string Content { get; init; } = string.Empty;

    /// <summary>메시지 발신자 표시 이름. Role.User 메시지에만 설정. LLM 입력 포맷 및 mention 해석에 사용.</summary>
    public string? SenderName { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    // Tool 전용 필드
    /// <summary>Tool 호출 식별자. ToolCall/ToolResponse 쌍 연결에 사용. Role.ToolCall, Role.ToolResponse에만 설정.</summary>
    public Guid? CallId { get; init; }

    /// <summary>Tool 이름. Role.ToolCall, Role.ToolResponse에서 사용.</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool 호출 인자. Role.ToolCall에서만 사용. JSON 문자열로 저장 (원상 복원 가능).</summary>
    public string? ToolArgsJson { get; init; }

    /// <summary>
    /// Provider별 보조 메타데이터 JSON. Gemini의 thought_signature 등 텍스트로 표현 안 되는
    /// Part 데이터 보존용. Role.Assistant 또는 Role.ToolCall 메시지에서 같은 model 응답 turn에
    /// 동반된 non-text/non-FunctionCall Parts(예: thought)를 직렬화해 저장.
    /// 다음 호출 시 BuildContents에서 역직렬화해 Content.Parts에 복원, cache hit 및 thinking
    /// reasoning context 유지에 사용.
    /// </summary>
    public string? ProviderMetadataJson { get; init; }
}

/// <summary>메시지 발화자 역할.</summary>
public enum Role
{
    System,
    User,
    Assistant,

    /// <summary>LLM의 FunctionCall 요청 (model 역할). Gemini API 규약에 따라 model로 매핑.</summary>
    ToolCall,

    /// <summary>Tool 실행 결과 (user 역할). Gemini API 규약에 따라 user로 매핑.</summary>
    ToolResponse
}
