using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>LLM 공급자 추상화. 구현체 교체 시 호출부 변경 없음.</summary>
public interface ILLMProvider
{
    string ProviderName { get; }

    /// <summary>
    /// 메시지 목록으로 LLM 응답을 생성한다.
    /// 단일 텍스트 응답이면 1개, 차후 tool use 등으로 복수 응답이면 N개.
    /// </summary>
    Task<IReadOnlyList<LLMResponse>> GenerateAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default);
}
