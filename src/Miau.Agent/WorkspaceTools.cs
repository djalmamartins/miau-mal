using System.Diagnostics;

namespace Miau.Agent;

public sealed class WorkspaceTools
{
    private readonly string _root;
    public WorkspaceTools(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        _root = Path.GetFullPath(workspace);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);
    }

    public string ListFiles(string path = ".") => string.Join('\n',
        Directory.EnumerateFileSystemEntries(Resolve(path)).OrderBy(x => x).Take(500)
            .Select(x => Path.GetRelativePath(_root, x)));

    public async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        var file = Resolve(path);
        if (!File.Exists(file)) throw new FileNotFoundException(path);
        return await File.ReadAllTextAsync(file, ct);
    }

    public async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var file = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content, ct);
        return $"written: {Path.GetRelativePath(_root, file)}";
    }

    public string Search(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(IsTextFile).Take(3000))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{Path.GetRelativePath(_root, file)}:{n}: {line.Trim()}");
                if (hits.Count >= 200) return string.Join('\n', hits);
            }
        }
        return string.Join('\n', hits);
    }

    public Task<string> RunCommandAsync(string command, CancellationToken ct) => RunAsync(command, ct);
    public Task<string> GitStatusAsync(CancellationToken ct) => RunAsync("git status --short --branch", ct);
    public Task<string> GitDiffAsync(CancellationToken ct) => RunAsync("git diff -- .", ct);
    public Task<string> GitLogAsync(CancellationToken ct) => RunAsync("git log --oneline -20", ct);

    private async Task<string> RunAsync(string command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh", $"-lc \"{command.Replace("\"", "\\\"")}\"");
        psi.WorkingDirectory = _root; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start command.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return $"exit={p.ExitCode}\n{await stdout}{await stderr}";
    }

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (full != _root && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes workspace.");
        return full;
    }

    private static bool IsTextFile(string path) =>
        new[]{".cs",".csproj",".json",".md",".txt",".xml",".yml",".yaml",".js",".ts",".tsx",".jsx",".css",".html",".py",".ps1",".sh"}
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
