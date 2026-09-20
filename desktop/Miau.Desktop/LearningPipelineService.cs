using System.Text.Json;

namespace Miau.Desktop;

public enum TrainingVerdict { Approved, Rejected }
public sealed record TrainingFeedback(string TaskId, TrainingVerdict Verdict, DateTimeOffset At, string? Note = null);
public sealed record LearningReadiness(int Approved, int PendingReview, int Rejected, int RecoveryExamples, int DistinctProjects, bool ReadyForLora, int MinimumRecommended = 100);

public sealed class LearningPipelineService
{
    readonly string appData;
    public LearningPipelineService(string? appData = null) => this.appData = appData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU");
    string Dataset => Path.Combine(appData, "dataset");
    string FeedbackFile => Path.Combine(Dataset, "feedback", "training-feedback-v1.jsonl");

    public async Task SetVerdictAsync(string taskId, TrainingVerdict verdict, string? note, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FeedbackFile)!);
        var feedback = new TrainingFeedback(taskId, verdict, DateTimeOffset.UtcNow, DatasetService.Redact(note ?? ""));
        await File.AppendAllTextAsync(FeedbackFile, JsonSerializer.Serialize(feedback) + Environment.NewLine, ct);
    }

    public async Task<LearningReadiness> GetReadinessAsync(CancellationToken ct)
    {
        var records = await ReadRecordsAsync(ct); var verdicts = await VerdictsAsync(ct);
        var approved = records.Count(x => x.QualityScore >= DatasetService.CompletedQualityThreshold && verdicts.TryGetValue(x.TaskId, out var verdict) && verdict == TrainingVerdict.Approved);
        var rejected = verdicts.Values.Count(x => x == TrainingVerdict.Rejected);
        var pending = records.Count(x => x.QualityScore >= DatasetService.CompletedQualityThreshold && !verdicts.ContainsKey(x.TaskId));
        var recoveries = CountLines(Path.Combine(Dataset, "recovery", "miau-recovery-v1.jsonl"));
        var projects = records.Where(x => verdicts.TryGetValue(x.TaskId, out var verdict) && verdict == TrainingVerdict.Approved).Select(x => x.ProjectFingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new(approved, pending, rejected, recoveries, projects, approved >= 100 && projects >= 3);
    }

    public async Task<string> ExportApprovedForLoraAsync(string outputDirectory, CancellationToken ct)
    {
        var verdicts = await VerdictsAsync(ct);
        var records = (await ReadRecordsAsync(ct)).Where(x => x.QualityScore >= DatasetService.CompletedQualityThreshold && verdicts.TryGetValue(x.TaskId, out var verdict) && verdict == TrainingVerdict.Approved).ToArray();
        Directory.CreateDirectory(outputDirectory);
        var file = Path.Combine(outputDirectory, "miau-lora-train.jsonl");
        var lines = records.Select(x => JsonSerializer.Serialize(new { instruction = x.Task, input = string.Join("\n", x.Plan), output = x.FinalResult, metadata = new { x.BaseModel, x.PromptVersion, x.QualityScore, x.Origin } }));
        await File.WriteAllLinesAsync(file, lines, ct);
        var readme = Path.Combine(outputDirectory, "README.md");
        await File.WriteAllTextAsync(readme, $"# MIAU LoRA export\n\nExemplos aprovados: {records.Length}\n\nEste arquivo é um dataset curado para fine-tuning/LoRA externo. O MIAU não treina nem instala modelos automaticamente. Valide o dataset, treine fora do aplicativo e carregue o adaptador resultante no Ollama.\n", ct);
        return file;
    }

    async Task<Dictionary<string, TrainingVerdict>> VerdictsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, TrainingVerdict>(StringComparer.OrdinalIgnoreCase); if (!File.Exists(FeedbackFile)) return map;
        foreach (var line in await File.ReadAllLinesAsync(FeedbackFile, ct))
            try { var item = JsonSerializer.Deserialize<TrainingFeedback>(line); if (item is not null) map[item.TaskId] = item.Verdict; } catch { }
        return map;
    }
    async Task<List<TrainingRecord>> ReadRecordsAsync(CancellationToken ct)
    {
        var result = new List<TrainingRecord>();
        foreach (var folder in new[] { "completed", "review" })
        {
            var directory = Path.Combine(Dataset, folder); if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                foreach (var line in await File.ReadAllLinesAsync(file, ct)) try { var item = JsonSerializer.Deserialize<TrainingRecord>(line); if (item is not null) result.Add(item); } catch { }
        }
        return result.GroupBy(x => x.TaskId, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()).ToList();
    }
    static int CountLines(string file) => File.Exists(file) ? File.ReadLines(file).Count() : 0;
}
