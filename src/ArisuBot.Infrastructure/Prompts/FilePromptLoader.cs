using ArisuBot.Core.Interfaces;

namespace ArisuBot.Infrastructure.Prompts;

/// <summary>파일 시스템에서 프롬프트를 로드한다. Reload() 호출 시 파일을 다시 읽어 캐시 갱신.</summary>
public class FilePromptLoader : IPromptLoader
{
    private readonly string _promptsDirectory;

    public string SystemPrompt { get; private set; } = string.Empty;
    public string PersonaPrompt { get; private set; } = string.Empty;

    /// <summary>생성 시 즉시 파일을 읽는다. 파일 없으면 FileNotFoundException (fail-fast).</summary>
    public FilePromptLoader(string promptsDirectory)
    {
        _promptsDirectory = promptsDirectory;
        Reload();
    }

    /// <summary>system.md, persona.md를 파일에서 다시 읽어 캐시를 갱신한다.</summary>
    public void Reload()
    {
        SystemPrompt = File.ReadAllText(Path.Combine(_promptsDirectory, "system.md"));
        PersonaPrompt = File.ReadAllText(Path.Combine(_promptsDirectory, "persona.md"));
    }
}
