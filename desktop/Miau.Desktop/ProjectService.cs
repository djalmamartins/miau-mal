using System.Diagnostics;

namespace Miau.Desktop;

public sealed class ProjectService
{
    public async Task<ProjectInfo> InspectAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var isGit = Directory.Exists(Path.Combine(path, ".git"));
        var branch = isGit ? (await Git(path, "branch --show-current", ct)).Trim() : "";
        var remote = isGit ? (await Git(path, "remote get-url origin", ct)).Trim() : "";
        return new(name, path, isGit, branch, remote);
    }

    static async Task<string> Git(string root, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null) return "";
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output;
    }
}

public sealed record ProjectInfo(string Name, string Path, bool IsGit, string Branch, string Remote);