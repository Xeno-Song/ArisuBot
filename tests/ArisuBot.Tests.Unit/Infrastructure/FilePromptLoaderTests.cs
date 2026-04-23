using ArisuBot.Infrastructure.Prompts;

namespace ArisuBot.Tests.Unit.Infrastructure;

public class FilePromptLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public FilePromptLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private void WritePrompts(string system, string persona)
    {
        File.WriteAllText(Path.Combine(_tempDir, "system.md"), system);
        File.WriteAllText(Path.Combine(_tempDir, "persona.md"), persona);
    }

    [Fact]
    public void Constructor_LoadsPromptsFromFiles()
    {
        WritePrompts("sys content", "persona content");

        var loader = new FilePromptLoader(_tempDir);

        Assert.Equal("sys content", loader.SystemPrompt);
        Assert.Equal("persona content", loader.PersonaPrompt);
    }

    [Fact]
    public void Constructor_Throws_WhenSystemMdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "persona.md"), "persona");

        Assert.Throws<FileNotFoundException>(() => new FilePromptLoader(_tempDir));
    }

    [Fact]
    public void Constructor_Throws_WhenPersonaMdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "system.md"), "system");

        Assert.Throws<FileNotFoundException>(() => new FilePromptLoader(_tempDir));
    }

    [Fact]
    public void Reload_UpdatesPromptsFromFiles()
    {
        WritePrompts("original sys", "original persona");
        var loader = new FilePromptLoader(_tempDir);

        WritePrompts("updated sys", "updated persona");
        loader.Reload();

        Assert.Equal("updated sys", loader.SystemPrompt);
        Assert.Equal("updated persona", loader.PersonaPrompt);
    }

    [Fact]
    public void Reload_Throws_WhenFileDeletedAfterInit()
    {
        WritePrompts("sys", "persona");
        var loader = new FilePromptLoader(_tempDir);

        File.Delete(Path.Combine(_tempDir, "system.md"));

        Assert.Throws<FileNotFoundException>(() => loader.Reload());
    }
}
