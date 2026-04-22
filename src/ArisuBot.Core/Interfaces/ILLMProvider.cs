using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>LLM 공급자 추상화. 구현체 교체 시 호출부 변경 없음.</summary>
public interface ILLMProvider
{
    string ProviderName { get; }
    Task<LLMResponse> GenerateAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default);
}
