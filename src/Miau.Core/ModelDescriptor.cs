namespace Miau.Core;

public sealed record ModelDescriptor
{
    public ModelDescriptor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Model path must have the .gguf extension.", nameof(path));
        }

        Path = path;
    }

    // Existence and file contents are checked by the adapter at load time.
    public string Path { get; }
}
