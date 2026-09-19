using System.Text.Json;

namespace Miau.Desktop;

public sealed record RepairCandidate(string Id, string Reason, int EvidenceCount, string Prompt);
public sealed record RepairDecision(string Id, bool Accepted, string Reason, DateTimeOffset At);

public sealed class SelfRepairService
{
    readonly string appData;
    public SelfRepairService(string? appData = null) => this.appData = appData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU");

    public async Task<IReadOnlyList<RepairCandidate>> DetectAsync(CancellationToken ct)
    {
        var recovery = Path.Combine(appData, "dataset", "recovery");
        if (!Directory.Exists(recovery)) return [];
        var failures = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(recovery, "*.jsonl", SearchOption.AllDirectories))
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
                try { using var d=JsonDocument.Parse(line); var root=d.RootElement; var key=root.TryGetProperty("FailedTool",out var t)?t.ToString():"unknown"; failures[key]=failures.GetValueOrDefault(key)+1; } catch { }
        return failures.Where(x=>x.Value>=3).OrderByDescending(x=>x.Value)
            .Select(x=>new RepairCandidate($"self-repair-{Slug(x.Key)}", $"{x.Key} falhou repetidamente", x.Value,
                $"Analise as falhas recorrentes da ferramenta {x.Key}. Corrija apenas a implementação diretamente relacionada, preserve compatibilidade e execute build e testes."))
            .ToArray();
    }

    public async Task RecordDecisionAsync(RepairDecision decision, CancellationToken ct)
    {
        var dir=Path.Combine(appData,"self-repair"); Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir,"decisions.jsonl"),JsonSerializer.Serialize(decision)+Environment.NewLine,ct);
    }

    public static bool Accept(double before, double after, bool buildPassed, bool testsPassed)
        => buildPassed && testsPassed && after >= before;

    static string Slug(string s) => new(s.ToLowerInvariant().Select(c=>char.IsLetterOrDigit(c)?c:'-').ToArray());
}
