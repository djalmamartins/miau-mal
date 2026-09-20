using System.Security.Cryptography;

namespace Miau.Desktop;

public sealed record WorkspaceBaseline(IReadOnlyDictionary<string, string> Files)
{
    public IReadOnlyDictionary<string, string> TextFiles { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static WorkspaceBaseline Capture(string root)
    {
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(Relevant).ToArray();
        var files = paths.ToDictionary(x => Path.GetRelativePath(root, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))), StringComparer.OrdinalIgnoreCase);
        var text = paths.Where(x => new FileInfo(x).Length <= 512_000 && IsText(x)).ToDictionary(x => Path.GetRelativePath(root, x), File.ReadAllText, StringComparer.OrdinalIgnoreCase);
        return new(files) { TextFiles = text };
    }
    public IReadOnlyCollection<string> ChangesProducedNow(string root)
    {
        var current = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(Relevant).ToDictionary(x => Path.GetRelativePath(root, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))), StringComparer.OrdinalIgnoreCase);
        return current.Where(x => !Files.TryGetValue(x.Key, out var before) || before != x.Value).Select(x => x.Key).Concat(Files.Keys.Where(x => !current.ContainsKey(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    static bool Relevant(string path) { var value = path.Replace('\\','/'); return !new[] { "/.git/", "/bin/", "/obj/", "/node_modules/", "/__pycache__/" }.Any(value.Contains); }
    static bool IsText(string path) => new[] { ".cs", ".html", ".htm", ".css", ".js", ".ts", ".json", ".md", ".txt", ".xml", ".axaml", ".py" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
