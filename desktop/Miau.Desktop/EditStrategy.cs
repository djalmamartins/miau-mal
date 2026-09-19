namespace Miau.Desktop;

public enum EditMethod { Replace, Write, Patch }
public sealed record EditDecision(EditMethod Method, string Reason);

public sealed class EditPolicy
{
    readonly Dictionary<string, int> replaceFailures = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> forceStructuredEdit = new(StringComparer.OrdinalIgnoreCase);
    public EditDecision Choose(string path, bool fileExists, bool fileRead, int changedRegions, int fileCount, bool substantialRewrite)
    {
        if (!fileExists || substantialRewrite || forceStructuredEdit.Contains(path)) return new(EditMethod.Write, !fileExists ? "arquivo novo" : substantialRewrite ? "reescrita substancial" : "replace anterior ambíguo; edição estruturada obrigatória");
        if (fileCount > 1 || changedRegions > 1) return new(EditMethod.Patch, "múltiplas alterações localizadas");
        if (!fileRead || replaceFailures.GetValueOrDefault(path) >= 2) return new(EditMethod.Write, "replace inseguro ou repetidamente falho");
        return new(EditMethod.Replace, "alteração pequena em trecho conhecido e único");
    }
    public bool Allow(MiauAction action)
    {
        if (action.Action != ToolNames.ReplaceInFile) return true;
        var path = action.Arguments.GetValueOrDefault("path", ""); return !forceStructuredEdit.Contains(path) && replaceFailures.GetValueOrDefault(path) < 2;
    }
    public void Observe(MiauAction action, ToolResult result)
    {
        if (action.Action != ToolNames.ReplaceInFile || result.Success) return;
        var path = action.Arguments.GetValueOrDefault("path", "");
        replaceFailures[path] = replaceFailures.GetValueOrDefault(path) + 1;
        if (result.Error?.Contains("corresponde a", StringComparison.OrdinalIgnoreCase) == true)
            forceStructuredEdit.Add(path);
    }
}

public sealed record FailureExperience(DateTimeOffset Timestamp, string ProjectFingerprint, string Tool, string File, string ErrorType,
    string Message, string AttemptedStrategy, string? RecoveryStrategy, bool Recovered);

public sealed class FailureLearningService
{
    readonly string directory;
    public FailureLearningService(string? directory = null) => this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "dataset", "recovery");
    public async Task RecordAsync(string workspace, MiauAction action, ToolResult result, string? recovery, bool recovered, CancellationToken ct)
    {
        Directory.CreateDirectory(directory); var error = result.Error ?? "unknown";
        var item = new FailureExperience(DateTimeOffset.Now, DatasetService.Fingerprint(workspace), action.Action, action.Arguments.GetValueOrDefault("path", ""),
            Classify(error), DatasetService.Redact(error), DatasetService.Redact(string.Join(";", action.Arguments.Select(x => $"{x.Key}={x.Value}"))), recovery, recovered);
        await File.AppendAllTextAsync(Path.Combine(directory, "miau-recovery-v1.jsonl"), System.Text.Json.JsonSerializer.Serialize(item) + Environment.NewLine, ct);
    }
    static string Classify(string error) => error.Contains("exatamente uma vez", StringComparison.OrdinalIgnoreCase) ? "replace_mismatch" : error.Contains("fora do workspace", StringComparison.OrdinalIgnoreCase) ? "path_scope" : "tool_error";
}
