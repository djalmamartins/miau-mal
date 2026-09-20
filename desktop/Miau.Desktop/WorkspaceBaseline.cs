using System.Security.Cryptography;

namespace Miau.Desktop;

public sealed record WorkspaceBaseline(IReadOnlyDictionary<string, string> Files)
{
    public static WorkspaceBaseline Capture(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(Relevant).ToDictionary(x => Path.GetRelativePath(root, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))), StringComparer.OrdinalIgnoreCase);
        return new(files);
    }
    public IReadOnlyCollection<string> ChangesProducedNow(string root)
    {
        var current = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(Relevant).ToDictionary(x => Path.GetRelativePath(root, x), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))), StringComparer.OrdinalIgnoreCase);
        return current.Where(x => !Files.TryGetValue(x.Key, out var before) || before != x.Value).Select(x => x.Key).Concat(Files.Keys.Where(x => !current.ContainsKey(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    static bool Relevant(string path) { var value = path.Replace('\\','/'); return !new[] { "/.git/", "/bin/", "/obj/", "/node_modules/", "/__pycache__/" }.Any(value.Contains); }
}
