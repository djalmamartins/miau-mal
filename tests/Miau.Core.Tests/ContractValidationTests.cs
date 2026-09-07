using Miau.Core;

namespace Miau.Core.Tests;

public sealed class ContractValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("model.bin")]
    [InlineData("model.gguf.exe")]
    public void Model_rejects_invalid_paths(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ModelDescriptor(path!));
    }

    [Theory]
    [InlineData("models/small.gguf")]
    [InlineData("C:\\Models\\small.GGUF")]
    public void Descriptor_does_not_require_file_io(string path)
    {
        Assert.Equal(path, new ModelDescriptor(path).Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n")]
    public void Generation_rejects_empty_prompts(string? prompt)
    {
        Assert.ThrowsAny<ArgumentException>(() => new GenerationRequest(prompt!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Generation_rejects_nonpositive_token_limits(int maxTokens)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationRequest("Hello", maxTokens));
    }

    [Fact]
    public void Generation_preserves_prompt_and_explicit_limit()
    {
        var request = new GenerationRequest("  Hello\n", 32);
        Assert.Equal("  Hello\n", request.Prompt);
        Assert.Equal(32, request.MaxTokens);
    }
}
