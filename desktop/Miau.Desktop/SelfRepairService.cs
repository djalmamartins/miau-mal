using System.Diagnostics;
using System.Text.Json;

namespace Miau.Desktop;

public sealed record RepairCandidate(string Id, string Reason, int EvidenceCount, string Prompt);
public sealed record RepairDecision(string Id, bool Accepted, string Reason, DateTimeOffset At);
public sealed record RepairRunResult(string Id, bool Accepted, double BenchmarkBefore, double BenchmarkAfter, bool BuildPassed, bool TestsPassed, string Detail, string? PatchPath = null, string? BaseCommit = null);

public sealed class SelfRepairService
{
    readonly string appData;
    public SelfRepairService(string? appData = null) => this.appData = appData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU");

    public async Task<IReadOnlyList<RepairCandidate>> DetectAsync(CancellationToken ct, int evidenceThreshold = 3)
    {
        var recovery = Path.Combine(appData, "dataset", "recovery");
        if (!Directory.Exists(recovery)) return [];
        var failures = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(recovery, "*.jsonl", SearchOption.AllDirectories))
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
                try { using var d=JsonDocument.Parse(line); var root=d.RootElement; var key=root.TryGetProperty("FailedTool",out var t)?t.ToString():"unknown"; failures[key]=failures.GetValueOrDefault(key)+1; } catch { }
        return failures.Where(x=>x.Value>=Math.Max(2, evidenceThreshold)).OrderByDescending(x=>x.Value)
            .Select(x=>new RepairCandidate($"self-repair-{Slug(x.Key)}", $"{x.Key} falhou repetidamente", x.Value,
                $"Analise as falhas recorrentes da ferramenta {x.Key}. Corrija apenas a implementação diretamente relacionada, preserve compatibilidade e execute build e testes."))
            .ToArray();
    }

    public async Task<RepairRunResult> RunIsolatedAsync(string sourceRoot, RepairCandidate candidate, AgentService agent, string benchmarkManifest, CancellationToken ct, Action<string>? progress = null)
    {
        if (!Directory.Exists(Path.Combine(sourceRoot, ".git"))) throw new InvalidOperationException("Auto-reparo exige um workspace Git.");
        var temp = Path.Combine(Path.GetTempPath(), "miau-self-repair", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        progress?.Invoke($"RepairJob {candidate.Id}: criando workspace isolado.");
        try
        {
            await Run(sourceRoot, "git", ["clone", "-q", "--no-hardlinks", sourceRoot, temp], ct);
            var baseCommit = (await Run(temp, "git", ["rev-parse", "HEAD"], ct)).Trim();
            var manifest = Path.Combine(temp, Path.GetRelativePath(sourceRoot, benchmarkManifest));
            var before = await BenchmarkScore(agent, manifest, ct, progress, "antes");
            await agent.RunAsync(temp, candidate.Prompt, ct, x => progress?.Invoke(x));
            var build = await RunCheck(temp, "dotnet", ["build", "desktop/Miau.Desktop/Miau.Desktop.csproj"], ct);
            var tests = build && await RunCheck(temp, "dotnet", ["test", "desktop/Miau.Desktop.Tests/Miau.Desktop.Tests.csproj", "--no-restore"], ct);
            var after = build && tests ? await BenchmarkScore(agent, manifest, ct, progress, "depois") : 0;
            var accepted = Accept(before, after, build, tests);
            var detail = accepted ? "correção candidata aprovada pelo quality gate; workspace principal não foi alterado" : "correção rejeitada; regressão, build/teste ou benchmark não aprovado";
            string? patchPath = null;
            if (accepted)
            {
                var patch = await Run(temp, "git", ["diff", "--binary", "HEAD"], ct);
                if (!string.IsNullOrWhiteSpace(patch))
                {
                    var dir = Path.Combine(appData, "self-repair", "approved"); Directory.CreateDirectory(dir);
                    patchPath = Path.Combine(dir, $"{candidate.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.patch");
                    await File.WriteAllTextAsync(patchPath, patch, ct);
                }
            }
            await RecordDecisionAsync(new(candidate.Id, accepted, detail, DateTimeOffset.Now), ct);
            return new(candidate.Id, accepted, before, after, build, tests, detail, patchPath, baseCommit);
        }
        finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
    }

    public async Task<string> PromoteAsync(string sourceRoot, RepairRunResult repair, CancellationToken ct)
    {
        if (!repair.Accepted || string.IsNullOrWhiteSpace(repair.PatchPath) || !File.Exists(repair.PatchPath))
            throw new InvalidOperationException("Somente um reparo aprovado com patch preservado pode ser promovido.");
        var status = await Run(sourceRoot, "git", ["status", "--porcelain"], ct);
        if (!string.IsNullOrWhiteSpace(status)) throw new InvalidOperationException("Promoção exige working tree limpo.");
        var head = (await Run(sourceRoot, "git", ["rev-parse", "HEAD"], ct)).Trim();
        if (!string.Equals(head, repair.BaseCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O projeto mudou desde o RepairJob; promoção bloqueada para evitar patch obsoleto.");
        await Run(sourceRoot, "git", ["apply", "--check", repair.PatchPath], ct);
        await Run(sourceRoot, "git", ["apply", repair.PatchPath], ct);
        return await Run(sourceRoot, "git", ["diff", "--stat"], ct);
    }

    public async Task RevertPromotionAsync(string sourceRoot, RepairRunResult repair, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repair.PatchPath) || !File.Exists(repair.PatchPath)) throw new InvalidOperationException("Patch do reparo não encontrado.");
        await Run(sourceRoot, "git", ["apply", "--reverse", "--check", repair.PatchPath], ct);
        await Run(sourceRoot, "git", ["apply", "--reverse", repair.PatchPath], ct);
    }

    static async Task<double> BenchmarkScore(AgentService agent, string manifest, CancellationToken ct, Action<string>? progress, string phase)
    {
        if (!File.Exists(manifest)) throw new FileNotFoundException("Manifesto do benchmark não encontrado.", manifest);
        var results = await agent.RunBenchmarkAsync(manifest, ct);
        var score = results.Count == 0 ? 0 : Math.Round(results.Count(x => x.Passed) * 100d / results.Count, 1);
        progress?.Invoke($"Benchmark {phase}: {score:0.0}% ({results.Count(x=>x.Passed)}/{results.Count}).");
        return score;
    }

    public async Task RecordDecisionAsync(RepairDecision decision, CancellationToken ct)
    {
        var dir=Path.Combine(appData,"self-repair"); Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir,"decisions.jsonl"),JsonSerializer.Serialize(decision)+Environment.NewLine,ct);
    }

    public static bool Accept(double before, double after, bool buildPassed, bool testsPassed)
        => buildPassed && testsPassed && after >= before;

    static async Task<bool> RunCheck(string root, string command, string[] args, CancellationToken ct)
    {
        try { await Run(root, command, args, ct); return true; } catch { return false; }
    }
    static async Task<string> Run(string root, string command, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command) { WorkingDirectory=root, RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
        foreach(var arg in args) psi.ArgumentList.Add(arg);
        using var p=Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {command}.");
        var output=await p.StandardOutput.ReadToEndAsync(ct); var error=await p.StandardError.ReadToEndAsync(ct); await p.WaitForExitAsync(ct);
        if(p.ExitCode!=0) throw new InvalidOperationException(DatasetService.Redact(string.IsNullOrWhiteSpace(error)?output:error));
        return output;
    }
    static string Slug(string s) => new(s.ToLowerInvariant().Select(c=>char.IsLetterOrDigit(c)?c:'-').ToArray());
}
