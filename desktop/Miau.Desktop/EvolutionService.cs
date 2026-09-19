using System.Text.Json;

namespace Miau.Desktop;

public sealed record SkillMetric(string Name, int Attempts, int Successes, int Failures, double SuccessRate);
public sealed record EvolutionPoint(DateTimeOffset Timestamp, int TasksCompleted, double SuccessRate, double RetryRate, double RecoveryRate,
    double AverageQuality, double ToolFailureRate, double ScopeViolationRate, double BuildSuccessRate, double TestSuccessRate);
public sealed record EvolutionSnapshot(string AgentVersion, string Model, string ProtocolVersion, string DatasetVersion,
    int TasksTotal, int TasksCompleted, int TasksFailed, int TasksCancelled, double SuccessRate, int Experiences, int Memories,
    int Failures, int RecoveredFailures, int Retries, int BuildsPassed, int BuildsFailed, int TestsPassed, int TestsFailed,
    double AverageDurationSeconds, int DatasetCompleted, int DatasetRecovery, int DatasetRejected, int DatasetReview,
    double AverageQualityScore, IReadOnlyList<SkillMetric> Skills, IReadOnlyList<string> RecentActivity, IReadOnlyList<EvolutionPoint> History);

public sealed class EvolutionService
{
    readonly string appData;
    public EvolutionService(string? appData = null) => this.appData = appData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU");

    public async Task<EvolutionSnapshot> GetSnapshotAsync(string model, bool persistHistory = true, CancellationToken ct = default)
    {
        var dataset = Path.Combine(appData, "dataset"); var documents = await ReadDocuments(Path.Combine(dataset, "completed"), ct);
        documents.AddRange(await ReadDocuments(dataset, ct, topLevelOnly: true)); // legacy, preserved
        var recoveries = await ReadDocuments(Path.Combine(dataset, "recovery"), ct);
        var rejected = await ReadDocuments(Path.Combine(dataset, "rejected"), ct); var review = await ReadDocuments(Path.Combine(dataset, "review"), ct);
        var memories = await ReadDocuments(Path.Combine(appData, "memory"), ct);
        var completed = documents.Count; var failed = rejected.Count; var cancelled = rejected.Count(x => Text(x, "reason").Contains("cancel", StringComparison.OrdinalIgnoreCase));
        var retries = documents.Sum(x => Number(x, "Retries", "retries")); var quality = documents.Select(x => Decimal(x, "QualityScore", "quality_score")).Where(x => x > 0).ToArray();
        var durations = documents.Select(x => DurationSeconds(x)).Where(x => x > 0).ToArray();
        var toolEvents = documents.SelectMany(ToolResults).ToArray();
        var scopeViolations = rejected.Count(x => Text(x, "reason").Contains("escopo", StringComparison.OrdinalIgnoreCase) || Text(x, "reason").Contains("scope", StringComparison.OrdinalIgnoreCase));\n        var skills = BuildSkills(toolEvents, documents, recoveries, scopeViolations);
        var buildsPassed = CountTool(toolEvents, "build", true); var buildsFailed = CountTool(toolEvents, "build", false);
        var testsPassed = CountTool(toolEvents, "test", true); var testsFailed = CountTool(toolEvents, "test", false);
        var recovered = recoveries.Count(x => Bool(x, "Recovered", "recovered")); var total = completed + failed;
        var point = new EvolutionPoint(DateTimeOffset.Now, completed, Rate(completed, total), Rate(retries, Math.Max(completed, 1)), Rate(recovered, recoveries.Count),
            quality.Length == 0 ? 0 : quality.Average(), Rate(toolEvents.Count(x => !x.Success), toolEvents.Length),
            Rate(rejected.Count(x => Text(x, "reason").Contains("escopo", StringComparison.OrdinalIgnoreCase)), total),
            Rate(buildsPassed, buildsPassed + buildsFailed), Rate(testsPassed, testsPassed + testsFailed));
        var history = await LoadHistory(ct); if (persistHistory && (history.Count == 0 || Changed(history[^1], point))) { history.Add(point); await SaveHistory(history, ct); }
        var recent = documents.Concat(rejected).OrderByDescending(Timestamp).Take(12).Select(x => SummarizeActivity(x)).ToArray();
        return new(Miau1Coder.AgentName, model, Miau1Coder.ProtocolVersion, Miau1Coder.DatasetSchemaVersion, total, completed, failed, cancelled,
            Rate(completed, total), documents.Count + recoveries.Count + rejected.Count, memories.Count, recoveries.Count, recovered, retries,
            buildsPassed, buildsFailed, testsPassed, testsFailed, durations.Length == 0 ? 0 : durations.Average(), completed, recoveries.Count,
            rejected.Count, review.Count, quality.Length == 0 ? 0 : quality.Average(), skills, recent, history);
    }

    async Task<List<JsonElement>> ReadDocuments(string directory, CancellationToken ct, bool topLevelOnly = false)
    {
        var result = new List<JsonElement>(); if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories))
            foreach (var line in await File.ReadAllLinesAsync(file, ct)) try { using var doc = JsonDocument.Parse(line); result.Add(doc.RootElement.Clone()); } catch { }
        return result;
    }
    static IEnumerable<(string Name, bool Success)> ToolResults(JsonElement doc)
    {
        if (!Try(doc, out var array, "ToolResults", "tool_results") || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in array.EnumerateArray()) yield return (Text(x, "Name", "name"), Bool(x, "Success", "success"));
    }
    static IReadOnlyList<SkillMetric> BuildSkills((string Name, bool Success)[] tools, List<JsonElement> docs, List<JsonElement> recoveries, int scopeViolations)
    {
        var groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["Leitura"] = ["read_file", "list_files", "search"], ["Edição"] = ["write_file", "replace_in_file", "apply_patch"], ["Build"] = ["build"], ["Testes"] = ["test"], ["Git"] = ["git_status", "git_diff"], ["Recovery"] = [], ["Scope"] = [] };
        return groups.Select(g => { var attempts = g.Key == "Recovery" ? docs.Sum(x => Number(x, "Retries", "retries")) : g.Key == "Scope" ? docs.Count : tools.Count(x => g.Value.Contains(x.Name, StringComparer.OrdinalIgnoreCase)); var failures = g.Key == "Recovery" ? 0 : g.Key == "Scope" ? 0 : tools.Count(x => g.Value.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && !x.Success); return new SkillMetric(g.Key, attempts, Math.Max(0, attempts - failures), failures, Rate(Math.Max(0, attempts - failures), attempts)); }).ToArray();
    }
    static string SummarizeActivity(JsonElement x)\n    {\n        var at = Timestamp(x); var raw = DatasetService.Redact(Text(x, "Task", "request", "FinalResult", "reason")).Replace("\\r", " ").Replace("\\n", " ").Trim();\n        if (raw.Length > 180) raw = raw[..180] + "…";\n        return $"{(at == DateTimeOffset.MinValue ? "sem data" : at.ToString("g"))} · {raw}";\n    }\n    static int CountTool((string Name, bool Success)[] tools, string name, bool success) => tools.Count(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Success == success);
    async Task<List<EvolutionPoint>> LoadHistory(CancellationToken ct) { var file = Path.Combine(appData, "evolution", "history.jsonl"); if (!File.Exists(file)) return []; var result = new List<EvolutionPoint>(); foreach (var line in await File.ReadAllLinesAsync(file, ct)) try { var x = JsonSerializer.Deserialize<EvolutionPoint>(line); if (x is not null) result.Add(x); } catch { } return result; }
    async Task SaveHistory(IEnumerable<EvolutionPoint> history, CancellationToken ct) { var dir = Path.Combine(appData, "evolution"); Directory.CreateDirectory(dir); await File.WriteAllLinesAsync(Path.Combine(dir, "history.jsonl"), history.TakeLast(500).Select(x => JsonSerializer.Serialize(x)), ct); }
    static bool Changed(EvolutionPoint a, EvolutionPoint b) => a.TasksCompleted != b.TasksCompleted || a.AverageQuality != b.AverageQuality || a.SuccessRate != b.SuccessRate;
    static DateTimeOffset Timestamp(JsonElement x) { var value = Text(x, "Timestamp", "timestamp", "At"); return DateTimeOffset.TryParse(value, out var at) ? at : DateTimeOffset.MinValue; }
    static double DurationSeconds(JsonElement x) { var value = Text(x, "Duration", "duration"); return TimeSpan.TryParse(value, out var d) ? d.TotalSeconds : 0; }
    static int Number(JsonElement x, params string[] names) => Try(x, out var v, names) && v.TryGetInt32(out var n) ? n : 0;
    static double Decimal(JsonElement x, params string[] names) => Try(x, out var v, names) && v.TryGetDouble(out var n) ? n : 0;
    static bool Bool(JsonElement x, params string[] names) => Try(x, out var v, names) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
    static string Text(JsonElement x, params string[] names) => Try(x, out var v, names) ? v.ToString() : "";
    static bool Try(JsonElement x, out JsonElement value, params string[] names) { foreach (var name in names) if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out value)) return true; value = default; return false; }
    static double Rate(int success, int attempts) => attempts <= 0 ? 0 : Math.Round(success * 100d / attempts, 1);
}
