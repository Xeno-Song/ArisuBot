namespace ArisuBot.Core.Interfaces;

/// <summary>프롬프트 파일을 로드하고 캐싱한다. /new-session 시 Reload()로 갱신.</summary>
public interface IPromptLoader
{
    /// <summary>system.md 내용. Role.System으로 매 요청 prepend.</summary>
    string SystemPrompt { get; }

    /// <summary>persona.md 내용. 컨텍스트 최초 생성 시 Role.User로 1회 주입.</summary>
    string PersonaPrompt { get; }

    /// <summary>파일을 다시 읽어 캐시를 갱신한다. /new-session 호출 시 사용.</summary>
    void Reload();
}
