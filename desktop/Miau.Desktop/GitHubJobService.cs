using System.Diagnostics;
using System.Text.Json;

namespace Miau.Desktop;

public sealed record TaskDelivery(string Commit, string PullRequestUrl);

public sealed class GitHubJobService
{
    public async Task<AgentJob?> AcquireNextAsync(string root, string agentId, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) || !await HasGhAsync(root, ct)) return null;
        await EnsureLabelsAsync(root, ct);
        var json = await Run(root, "gh", ["issue","list","--state","open","--label","miau-ready","--limit","20","--json","number,title,body,labels,url"], ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var issue in doc.RootElement.EnumerateArray())
        {
            var labels = issue.GetProperty("labels").EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToArray();
            if (labels.Any(x => x.StartsWith("miau-claimed-", StringComparison.OrdinalIgnoreCase))) continue;
            var number = issue.GetProperty("number").GetInt32();
            var title = issue.GetProperty("title").GetString() ?? $"Issue #{number}";
            var body = issue.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var claim = $"miau-claimed-{Slug(agentId)}";
            await Run(root, "gh", ["label","create",claim,"--color","6f42c1","--force"], ct);
            await Run(root, "gh", ["issue","edit",number.ToString(),"--add-label",claim,"--remove-label","miau-ready"], ct);

            var verify = await Run(root, "gh", ["issue","view",number.ToString(),"--json","labels"], ct);
            using var vd = JsonDocument.Parse(verify);
            var claims = vd.RootElement.GetProperty("labels").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString() ?? "")
                .Where(x => x.StartsWith("miau-claimed-", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x).ToArray();
            if (claims.Length != 1 || !claims[0].Equals(claim, StringComparison.OrdinalIgnoreCase))
            {
                await Run(root, "gh", ["issue","edit",number.ToString(),"--remove-label",claim], ct, false);
                continue;
            }
            var branch = $"miau/{Slug(agentId)}/{number}-{Slug(title,42)}";
            return new AgentJob(number.ToString(), $"#{number} {title}", $"Implemente a GitHub issue #{number}.\n\n{body}", branch);
        }
        return null;
    }

    public async Task PrepareBranchAsync(string root, AgentJob job, CancellationToken ct)
    {
        var dirty = await Run(root, "git", ["status","--porcelain"], ct);
        if (!string.IsNullOrWhiteSpace(dirty)) throw new InvalidOperationException("Working tree não está limpo. MIAU se recusa a misturar tarefas. Finalmente algum bom senso.");
        await Run(root, "git", ["fetch","origin"], ct);
        var baseBranch = (await Run(root, "git", ["remote","show","origin"], ct)).Split('\n')
            .FirstOrDefault(x => x.Contains("HEAD branch:"))?.Split(':',2)[1].Trim() ?? "main";
        await Run(root, "git", ["switch",baseBranch], ct);
        await Run(root, "git", ["pull","--ff-only","origin",baseBranch], ct);
        if (string.IsNullOrWhiteSpace(job.Branch)) return;
        var exists = await Run(root, "git", ["branch","--list",job.Branch], ct);
        await Run(root, "git", string.IsNullOrWhiteSpace(exists) ? ["switch","-c",job.Branch] : ["switch",job.Branch], ct);
    }

    public async Task<TaskDelivery> DeliverAsync(string root, AgentJob job, string summary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await Run(root, "git", ["status","--porcelain"], ct)))
            throw new InvalidOperationException("A tarefa terminou sem alterações para entregar.");
        await Run(root, "git", ["add","-A"], ct);
        await Run(root, "git", ["commit","-m",$"feat(miau): complete issue #{job.Id}"], ct);
        var sha = (await Run(root, "git", ["rev-parse","HEAD"], ct)).Trim();
        await Run(root, "git", ["push","-u","origin",job.Branch!], ct);
        var pr = (await Run(root, "gh", ["pr","create","--title",job.Title,"--body",$"Executado pelo MIAU.\n\n{summary}\n\nCloses #{job.Id}"], ct)).Trim();
        return new TaskDelivery(sha, pr);
    }

    public async Task CompleteAsync(string root, AgentJob job, string agentId, string report, CancellationToken ct)
    {
        await Run(root, "gh", ["issue","comment",job.Id,"--body",report], ct);
        await SetOutcome(root, job, agentId, "miau-done", ct);
    }

    public async Task FailAsync(string root, AgentJob job, string agentId, string report, CancellationToken ct)
    {
        await Run(root, "gh", ["issue","comment",job.Id,"--body",report], ct, false);
        await SetOutcome(root, job, agentId, "miau-failed", ct);
    }

    async Task SetOutcome(string root, AgentJob job, string agentId, string label, CancellationToken ct)
    {
        await Run(root, "gh", ["issue","edit",job.Id,"--remove-label",$"miau-claimed-{Slug(agentId)}","--add-label",label], ct);
    }

    async Task EnsureLabelsAsync(string root, CancellationToken ct)
    {
        foreach (var (name,color) in new[]{("miau-ready","1f883d"),("miau-done","8250df"),("miau-failed","cf222e")})
            await Run(root,"gh",["label","create",name,"--color",color,"--force"],ct,false);
    }

    static async Task<bool> HasGhAsync(string root, CancellationToken ct)
    {
        try { await Run(root,"gh",["auth","status"],ct); return true; } catch { return false; }
    }

    static string Slug(string value, int max=28)
    {
        var s=new string(value.ToLowerInvariant().Select(c=>char.IsLetterOrDigit(c)?c:'-').ToArray());
        while(s.Contains("--")) s=s.Replace("--","-");
        s=s.Trim('-'); return s.Length<=max?s:s[..max].Trim('-');
    }

    static async Task<string> Run(string root,string exe,IEnumerable<string> args,CancellationToken ct,bool throwOnError=true)
    {
        var psi=new ProcessStartInfo(exe){WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var arg in args) psi.ArgumentList.Add(arg);
        using var p=Process.Start(psi)??throw new InvalidOperationException($"Não foi possível iniciar {exe}.");
        var o=p.StandardOutput.ReadToEndAsync(ct); var e=p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct); var stdout=await o; var stderr=await e;
        if(throwOnError&&p.ExitCode!=0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)?$"{exe} saiu com código {p.ExitCode}.":stderr.Trim());
        return stdout+(string.IsNullOrWhiteSpace(stderr)?"":"\n"+stderr);
    }
}
