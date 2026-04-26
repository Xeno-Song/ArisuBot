using System.Collections.Concurrent;
using ArisuBot.Core.Interfaces;

namespace ArisuBot.LLM.Services;

/// <summary>
/// Tool 활성화 상태 in-memory 관리. IToolStateService 구현체.
/// 재시작 시 모든 tool이 활성화(true) 상태로 초기화된다.
/// </summary>
public class ToolStateService : IToolStateService
{
    // toolName → enabled. 기본값 없음 → IsEnabled에서 true 반환 (미등록 = 활성)
    private readonly ConcurrentDictionary<string, bool> _states = new();

    /// <inheritdoc/>
    public bool IsEnabled(string toolName)
        => _states.GetValueOrDefault(toolName, defaultValue: true);

    /// <inheritdoc/>
    public void SetEnabled(string toolName, bool enabled)
        => _states[toolName] = enabled;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, bool> GetAll()
        => _states.ToDictionary(kv => kv.Key, kv => kv.Value);
}
