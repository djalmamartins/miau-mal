using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miau.Desktop;

public sealed record TaskTraceEvent(DateTimeOffset At, string Kind, string Name, bool Success, string Detail);
public sealed record TrainingRecord(
    string TaskId, DateTimeOffset Timestamp, string AgentVersion, string BaseModel, string ProjectFingerprint,
    string Task, IReadOnlyList<string> Plan, IReadOnlyList<TaskTraceEvent> Actions,
    IReadOnlyList<TaskTraceEvent> ToolResults, IReadOnlyCollection<string> FilesInspected,
    IReadOnlyCollection<string> FilesChanged, IReadOnlyList<string> Patches,
    string? BuildResult, string? TestsResult, IReadOnlyList<string> Errors, int Retries, string FinalResult)
{
    public string AgentVersionName { get; init; } = Miau1Coder.AgentName;
    public string PromptVersion { get; init; } = Miau1Coder.PromptVersion;
    public string ProtocolVersion { get; init; } = Miau1Coder.ProtocolVersion;
    public string SchemaVersion { get; init; } = Miau1Coder.DatasetSchemaVersion;
    public TimeSpan Duration { get; init; }
    public bool Success { get; init; } = true;
    public double QualityScore { get; init; }
    public IReadOnlyList<string> QualityReasons { get; init; } = [];
    public string? RecoveryStrategy { get; init; }
    public string Origin { get; init; } = "interactive";
    public bool HumanIntervention { get; init; }
    public bool FinalBuildPassed { get; init; }
    public IReadOnlyList<VisualEvidence> VisualEvidence { get; init; } = [];
}

public interface IDatasetService { Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct); }

public sealed class DatasetService : IDatasetService
{
    public const double CompletedQualityThreshold = 70;
    static readonly Regex AssignmentSecret = new(@"(?i)\b(api[_-]?key|token|password|secret|authorization)\b\s*[:=]\s*[^\s,;""']+", RegexOptions.Compiled);
    static readonly Regex Authorization = new(@"(?im)^\s*authorization\s*:\s*.+$", RegexOptions.Compiled);
    static readonly Regex PrivateKey = new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled);
    static readonly Regex ConnectionString = new(@"(?i)\b(server|data source)\s*=.*?;.*?\b(password|pwd)\s*=.*?(?:;|$)", RegexOptions.Compiled);
    readonly string directory;
    public DatasetService(string? directory = null) => this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "dataset");
    public async Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var quality = DatasetQuality.Evaluate(record);
        var safe = record with
        {
            Task = Redact(record.Task), FinalResult = Redact(record.FinalResult), Patches = record.Patches.Select(Redact).ToArray(),
            Actions = Sanitize(record.Actions), ToolResults = Sanitize(record.ToolResults),
            BuildResult = Redact(record.BuildResult ?? ""), TestsResult = Redact(record.TestsResult ?? ""), Errors = record.Errors.Select(Redact).ToArray(),
            QualityScore = quality.Score, QualityReasons = quality.Reasons
        };
        var bucketName = safe.Success && safe.QualityScore >= CompletedQualityThreshold ? "completed" : "review";
        var bucket = Path.Combine(directory, bucketName); Directory.CreateDirectory(bucket);
        await File.AppendAllTextAsync(Path.Combine(bucket, "miau1-coder-v0.jsonl"), JsonSerializer.Serialize(safe) + Environment.NewLine, ct);
    }
    static TaskTraceEvent[] Sanitize(IEnumerable<TaskTraceEvent> events) => events.Select(x => x with { Detail = Redact(Summarize(x.Detail)) }).ToArray();
    static string Summarize(string value) => value.Length <= 4000 ? value : value[..4000] + "\n[truncated]";
    public static string Redact(string value)
    {
        value = PrivateKey.Replace(value, "[PRIVATE KEY REDACTED]");
        value = Authorization.Replace(value, "Authorization: [REDACTED]");
        value = ConnectionString.Replace(value, "[CONNECTION STRING REDACTED]");
        return AssignmentSecret.Replace(value, "$1=[REDACTED]");
    }
    public static string Fingerprint(string workspace)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace)));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public async Task<int> ExportHighQualityAsync(string outputDirectory, double minimumScore, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory); var output = Path.Combine(outputDirectory, "miau1-coder-v0.jsonl"); var count = 0;
        var source = Path.Combine(directory, "completed", "miau1-coder-v0.jsonl"); if (!File.Exists(source)) return 0;
        foreach (var line in await File.ReadAllLinesAsync(source, ct))
        { var item = JsonSerializer.Deserialize<TrainingRecord>(line); if (item is null || item.QualityScore < minimumScore) continue; await File.AppendAllTextAsync(output, line + Environment.NewLine, ct); count++; }
        return count;
    }

    public async Task SaveRejectedAsync(string task, string model, string reason, CancellationToken ct)
    {
        var rejected = Path.Combine(directory, "rejected"); Directory.CreateDirectory(rejected);
        var item = new { schema_version = Miau1Coder.DatasetSchemaVersion, timestamp = DateTimeOffset.Now, model, request = Redact(task), reason = Redact(reason), success = false };
        await File.AppendAllTextAsync(Path.Combine(rejected, "miau-rejected-v1.jsonl"), JsonSerializer.Serialize(item) + Environment.NewLine, ct);
    }
}

public sealed record DatasetQualityResult(double Score, IReadOnlyList<string> Reasons);
public static class DatasetQuality
{
    public static DatasetQualityResult Evaluate(TrainingRecord record)
    {
        var score = record.Success ? 50d : 0d; var reasons = new List<string>();
        if (record.Success) reasons.Add("tarefa concluída");
        if (record.FilesChanged.Count > 0) { score += 15; reasons.Add("diff/arquivos alterados"); }
        if (!string.IsNullOrWhiteSpace(record.BuildResult)) { score += 15; reasons.Add("build registrado"); }
        if (!string.IsNullOrWhiteSpace(record.TestsResult)) { score += 15; reasons.Add("testes registrados"); }
        score -= Math.Min(record.Retries * 5, 25); if (record.Retries > 0) reasons.Add($"-{record.Retries} retries");
        score -= Math.Min(record.Errors.Count * 3, 15); if (record.Errors.Count > 0) reasons.Add($"-{record.Errors.Count} erros");
        return new(Math.Clamp(score, 0, 100), reasons);
    }
}
