namespace Miau.Core;

public sealed record GenerationRequest
{
    public GenerationRequest(string prompt, int maxTokens = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        Prompt = prompt;
        MaxTokens = maxTokens;
    }

    public string Prompt { get; }
    public int MaxTokens { get; }
}
