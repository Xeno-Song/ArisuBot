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

    private void WritePrompts(
        string system,
        string persona,
        string compaction = "compaction content",
        string extraction = "extraction content",
        string compression = "compression content")
    {
        File.WriteAllText(Path.Combine(_tempDir, "system.md"), system);
        File.WriteAllText(Path.Combine(_tempDir, "persona.md"), persona);
        File.WriteAllText(Path.Combine(_tempDir, "compaction.md"), compaction);
        File.WriteAllText(Path.Combine(_tempDir, "semantic_memory_extraction.md"), extraction);
        File.WriteAllText(Path.Combine(_tempDir, "semantic_memory_compression.md"), compression);
    }

    [Fact]
    public void Constructor_LoadsPromptsFromFiles()
    {
        WritePrompts("sys content", "persona content", "compaction content", "extraction content", "compression content");

        var loader = new FilePromptLoader(_tempDir);

        Assert.Equal("sys content", loader.SystemPrompt);
        Assert.Equal("persona content", loader.PersonaPrompt);
        Assert.Equal("compaction content", loader.CompactionPrompt);
        Assert.Equal("extraction content", loader.SemanticMemoryExtractionPrompt);
        Assert.Equal("compression content", loader.SemanticMemoryCompressionPrompt);
    }

    [Fact]
    public void Constructor_Throws_WhenSystemMdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "persona.md"), "persona");
        File.WriteAllText(Path.Combine(_tempDir, "compaction.md"), "compaction");

        Assert.Throws<FileNotFoundException>(() => new FilePromptLoader(_tempDir));
    }

    [Fact]
    public void Constructor_Throws_WhenPersonaMdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "system.md"), "system");
        File.WriteAllText(Path.Combine(_tempDir, "compaction.md"), "compaction");

        Assert.Throws<FileNotFoundException>(() => new FilePromptLoader(_tempDir));
    }

    [Fact]
    public void Constructor_Throws_WhenCompactionMdMissing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "system.md"), "system");
        File.WriteAllText(Path.Combine(_tempDir, "persona.md"), "persona");

        Assert.Throws<FileNotFoundException>(() => new FilePromptLoader(_tempDir));
    }

    [Fact]
    public void Reload_UpdatesPromptsFromFiles()
    {
        WritePrompts("original sys", "original persona", "original compaction", "original extraction", "original compression");
        var loader = new FilePromptLoader(_tempDir);

        WritePrompts("updated sys", "updated persona", "updated compaction", "updated extraction", "updated compression");
        loader.Reload();

        Assert.Equal("updated sys", loader.SystemPrompt);
        Assert.Equal("updated persona", loader.PersonaPrompt);
        Assert.Equal("updated compaction", loader.CompactionPrompt);
        Assert.Equal("updated extraction", loader.SemanticMemoryExtractionPrompt);
        Assert.Equal("updated compression", loader.SemanticMemoryCompressionPrompt);
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
