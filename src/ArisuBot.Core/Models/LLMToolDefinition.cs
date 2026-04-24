namespace ArisuBot.Core.Models;

/// <summary>LLM에 등록할 툴의 정의. 이름, 설명, 파라미터 스키마를 포함한다.</summary>
public record LLMToolDefinition
{
    /// <summary>툴 이름. LLM이 호출 시 사용하는 식별자.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>툴 기능 설명. LLM이 툴 사용 여부를 판단하는 데 사용.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>파라미터 JSON Schema 문자열. OpenAPI 3.0 object schema 형식.</summary>
    public string ParametersJsonSchema { get; init; } = "{}";
}
