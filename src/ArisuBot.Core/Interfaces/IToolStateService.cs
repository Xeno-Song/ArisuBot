namespace ArisuBot.Core.Interfaces;

/// <summary>LLM Tool의 활성화/비활성화 상태를 관리한다. 상태는 in-memory이며 재시작 시 초기화된다.</summary>
public interface IToolStateService
{
    /// <summary>지정한 tool이 활성화 상태인지 반환한다. 등록되지 않은 tool은 true(활성)로 간주한다.</summary>
    bool IsEnabled(string toolName);

    /// <summary>지정한 tool의 활성화 상태를 설정한다.</summary>
    void SetEnabled(string toolName, bool enabled);

    /// <summary>현재 관리 중인 모든 tool 이름과 상태를 반환한다.</summary>
    IReadOnlyDictionary<string, bool> GetAll();
}
