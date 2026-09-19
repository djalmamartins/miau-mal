using System.Diagnostics;
using System.Text.Json;

namespace Miau.Desktop;

public sealed class GitHubJobService
{
    public async Task<AgentJob?> AcquireNextAsync(string root, string agentId, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(root, ".git"))) return null;
        if (!await HasGhAsync(root, ct)) return null;

        var json = await Cmd(root,
            "gh issue list --state open --label miau-ready --limit 20 --json number,title,body,labels,url", ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        foreach (var issue in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var number = issue.GetProperty("number").GetInt32();
            var title = issue.GetProperty("title").GetString() ?? $"Issue #{number}";
            var body = issue.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var labels = issue.GetProperty("labels").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString() ?? "")
                .ToArray();
            if (labels.Any(x => x.StartsWith("miau-claimed-", StringComparison.OrdinalIgnoreCase))) continue;

            var claim = $"miau-claimed-{Slug(agentId)}";
            await Cmd(root, $"gh label create {Q(claim)} --color 6f42c1 --force", ct);
            await Cmd(root, $"gh issue edit {number} --add-label {Q(claim)} --remove-label miau-ready", ct);

            // Re-read after claiming. If another worker won the race, abandon this issue.
            var verify = await Cmd(root, $"gh issue view {number} --json labels", ct);
            using var vd = JsonDocument.Parse(verify);
            var current = vd.RootElement.GetProperty("labels").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString() ?? "").ToArray();
            if (!current.Contains(claim, StringComparer.OrdinalIgnoreCase)) continue;

            var branch = $"miau/{Slug(agentId)}/{number}-{Slug(title, 42)}";
            return new AgentJob(number.ToString(), $"#{number} {title}",
                $"Implemente a GitHub issue #{number}.\n\n{body}", branch);
        }
        return null;
    }

    public async Task PrepareBranchAsync(string root, AgentJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.Branch)) return;
        await Cmd(root, "git fetch origin", ct);
        var exists = await Cmd(root, $"git branch --list {Q(job.Branch)}", ct);
        await Cmd(root, string.IsNullOrWhiteSpace(exists)
            ? $"git switch -c {Q(job.Branch)}"
            : $"git switch {Q(job.Branch)}", ct);
    }

    public async Task CompleteAsync(string root, AgentJob job, string agentId, CancellationToken ct)
    {
        var n = int.Parse(job.Id);
        var claim = $"miau-claimed-{Slug(agentId)}";
        await Cmd(root, $"gh issue edit {n} --remove-label {Q(claim)} --add-label miau-done", ct);
    }

    public async Task FailAsync(string root, AgentJob job, string agentId, CancellationToken ct)
    {
        var n = int.Parse(job.Id);
        var claim = $"miau-claimed-{Slug(agentId)}";
        await Cmd(root, $"gh issue edit {n} --remove-label {Q(claim)} --add-label miau-failed", ct);
    }

    static async Task<bool> HasGhAsync(string root, CancellationToken ct)
    {
        try { return (await Cmd(root, "gh auth status", ct, false)).Contains("Logged in", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string Slug(string value, int max = 28)
    {
        var s = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return s.Length <= max ? s : s[..max].Trim('-');
    }

    static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static async Task<string> Cmd(string root, string command, CancellationToken ct, bool throwOnError = true)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command.Replace("\"", "\\\"") + "\"")
            : new ProcessStartInfo("/bin/zsh", "-lc \"" + command.Replace("\"", "\\\"") + "\"");
        psi.WorkingDirectory = root;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o comando.");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var o = await output;
        var e = await error;
        if (throwOnError && p.ExitCode != 0) throw new InvalidOperationException(e.Trim());
        return o + (string.IsNullOrWhiteSpace(e) ? "" : "\n" + e);
    }
}
