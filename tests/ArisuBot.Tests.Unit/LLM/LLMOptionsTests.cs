using ArisuBot.LLM.Options;

namespace ArisuBot.Tests.Unit.LLM;

public class LLMOptionsTests
{
    [Fact]
    public void LLMOptions_DefaultProvider_IsGemini()
    {
        var opts = new LLMOptions();
        Assert.Equal("Gemini", opts.Provider);
    }

    [Fact]
    public void LLMOptions_DefaultMaxTokens_Is2048()
    {
        var opts = new LLMOptions();
        Assert.Equal(2048, opts.MaxTokens);
    }

    [Fact]
    public void LLMOptions_DefaultTemperature_Is07()
    {
        var opts = new LLMOptions();
        Assert.Equal(0.7f, opts.Temperature);
    }

    [Fact]
    public void GeminiOptions_DefaultModel_IsGemini25FlashLite()
    {
        var opts = new GeminiOptions();
        Assert.Equal("gemini-2.5-flash-lite", opts.Model);
    }

    [Fact]
    public void GeminiOptions_DefaultApiKey_IsEmpty()
    {
        var opts = new GeminiOptions();
        Assert.Equal(string.Empty, opts.ApiKey);
    }

    [Fact]
    public void GeminiOptions_SectionName_IsCorrect()
    {
        Assert.Equal("LLM:Gemini", GeminiOptions.SectionName);
    }

    [Fact]
    public void LLMOptions_SectionName_IsCorrect()
    {
        Assert.Equal("LLM", LLMOptions.SectionName);
    }
}
