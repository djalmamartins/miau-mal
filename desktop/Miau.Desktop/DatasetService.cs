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
    string? BuildResult, string? TestsResult, IReadOnlyList<string> Errors, int Retries, string FinalResult);

public interface IDatasetService { Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct); }

public sealed class DatasetService : IDatasetService
{
    static readonly Regex AssignmentSecret = new(@"(?i)\b(api[_-]?key|token|password|secret|authorization)\b\s*[:=]\s*[^\s,;""']+", RegexOptions.Compiled);
    static readonly Regex Authorization = new(@"(?im)^\s*authorization\s*:\s*.+$", RegexOptions.Compiled);
    static readonly Regex PrivateKey = new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled);
    static readonly Regex ConnectionString = new(@"(?i)\b(server|data source)\s*=.*?;.*?\b(password|pwd)\s*=.*?(?:;|$)", RegexOptions.Compiled);
    readonly string directory;
    public DatasetService(string? directory = null) => this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "dataset");
    public async Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var safe = record with
        {
            Task = Redact(record.Task), FinalResult = Redact(record.FinalResult), Patches = record.Patches.Select(Redact).ToArray(),
            Actions = Sanitize(record.Actions), ToolResults = Sanitize(record.ToolResults),
            BuildResult = Redact(record.BuildResult ?? ""), TestsResult = Redact(record.TestsResult ?? ""), Errors = record.Errors.Select(Redact).ToArray()
        };
        await File.AppendAllTextAsync(Path.Combine(directory, "miau1-coder-v0.jsonl"), JsonSerializer.Serialize(safe) + Environment.NewLine, ct);
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
}
