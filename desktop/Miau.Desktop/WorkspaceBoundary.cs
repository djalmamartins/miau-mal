using System.Text.RegularExpressions;

namespace Miau.Desktop;

public sealed record WorkspaceTransition(bool Authorized, string CurrentRoot, string? NewRoot, string Code, string Message);

public sealed class WorkspaceBoundary
{
    readonly HashSet<string> requestedDestinations;
    readonly string[] allowedRoots;
    public string InitialRoot { get; }
    public string CurrentRoot { get; private set; }
    public bool NewProjectRequested { get; }

    public WorkspaceBoundary(string initialRoot, string task, IEnumerable<string>? configuredAllowedRoots = null)
    {
        InitialRoot = Normalize(initialRoot); CurrentRoot = InitialRoot;
        NewProjectRequested = Regex.IsMatch(task, @"\b(crie|criar|novo|nova|inicialize|iniciar|create|new)\b[\s\S]{0,80}\b(projeto|project|site|pasta|diretório|diretorio)\b", RegexOptions.IgnoreCase);
        requestedDestinations = ExtractAbsolutePaths(task).Select(Normalize).ToHashSet(PathComparer);
        allowedRoots = (configuredAllowedRoots ?? DefaultAllowedRoots(InitialRoot)).Select(Normalize).Distinct(PathComparer).ToArray();
    }

    public WorkspaceTransition Initialize(string requestedPath)
    {
        string target;
        try { target = Normalize(requestedPath); }
        catch (Exception ex) { return Denied("InvalidPath", ex.Message); }
        if (!NewProjectRequested || !requestedDestinations.Contains(target)) return Denied("ExplicitRequestRequired", "Troca de workspace negada: o destino não foi solicitado explicitamente pelo usuário.");
        if (ContainsTraversal(requestedPath)) return Denied("PathTraversal", "Troca de workspace negada: path traversal não é permitido.");
        var allowedRoot = allowedRoots.FirstOrDefault(root => IsWithin(root, target));
        if (allowedRoot is null) return Denied("OutsideAllowedRoots", "Troca de workspace negada: destino fora das raízes permitidas.");
        try
        {
            EnsureNoSymlinkEscape(allowedRoot, target);
            Directory.CreateDirectory(target);
            EnsureNoSymlinkEscape(allowedRoot, target);
            CurrentRoot = target;
            return new(true, InitialRoot, target, "NewProjectWorkspaceAuthorized", $"NewProjectWorkspaceAuthorized\nWorkspaceRoot = {target}");
        }
        catch (Exception ex) { return Denied("UnsafeDestination", "Troca de workspace negada: " + ex.Message); }
    }

    public WorkspaceTransition InitializeForRequestedPath(string attemptedPath)
    {
        string normalized;
        try { normalized = Normalize(attemptedPath); } catch (Exception ex) { return Denied("InvalidPath", ex.Message); }
        var destination = requestedDestinations.OrderByDescending(x => x.Length).FirstOrDefault(x => IsWithin(x, normalized));
        return destination is null ? Denied("RequestedPathMismatch", "O caminho externo não pertence ao novo projeto solicitado.") : Initialize(destination);
    }

    WorkspaceTransition Denied(string code, string message) => new(false, CurrentRoot, null, code, message);
    static bool ContainsTraversal(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x == "..");
    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static bool IsWithin(string root, string path) => path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    static IEnumerable<string> ExtractAbsolutePaths(string task)
    {
        foreach (Match match in Regex.Matches(task, @"(?<![\w])/(?:[^\s`'""]+/?)+"))
            yield return match.Value.TrimEnd('.', ',', ';', ':');
    }
    static IEnumerable<string> DefaultAllowedRoots(string initial)
    {
        var configured = Environment.GetEnvironmentVariable("MIAU_ALLOWED_PROJECT_ROOTS");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var current = new DirectoryInfo(initial); current is not null; current = current.Parent)
            if (current.Name.Equals("Projects", StringComparison.OrdinalIgnoreCase)) return [current.FullName];
        return [Directory.GetParent(initial)?.FullName ?? initial];
    }
    static void EnsureNoSymlinkEscape(string allowedRoot, string target)
    {
        var current = allowedRoot;
        var relative = Path.GetRelativePath(allowedRoot, target);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null) throw new InvalidOperationException($"segmento simbólico não permitido: {current}");
        }
    }
    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
