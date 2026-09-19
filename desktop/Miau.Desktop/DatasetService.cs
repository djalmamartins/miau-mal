using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miau.Desktop;

public sealed record TaskTraceEvent(DateTimeOffset At, string Kind, string Name, bool Success, string Detail);
public sealed record TrainingRecord(string Task, string ProjectContext, IReadOnlyList<string> Plan, JobEvidence Evidence,
    IReadOnlyList<TaskTraceEvent> Events, string FinalResult);

public interface IDatasetService { Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct); }

public sealed class DatasetService : IDatasetService
{
    static readonly Regex Secret = new(@"(?i)(token|password|secret|api[_-]?key|authorization)\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled);
    readonly string directory;
    public DatasetService(string? directory = null) => this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "dataset");
    public async Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var safe = record with
        {
            Task = Redact(record.Task), ProjectContext = Redact(record.ProjectContext), FinalResult = Redact(record.FinalResult),
            Events = record.Events.Select(x => x with { Detail = Redact(x.Detail) }).ToArray()
        };
        await File.AppendAllTextAsync(Path.Combine(directory, "miau1-coder.jsonl"), JsonSerializer.Serialize(safe) + Environment.NewLine, ct);
    }
    static string Redact(string value) => Secret.Replace(value, "$1=[REDACTED]");
}
