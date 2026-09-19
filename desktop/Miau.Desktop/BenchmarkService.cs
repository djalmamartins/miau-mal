using System.Diagnostics;
using System.Text.Json;

namespace Miau.Desktop;

public sealed record BenchmarkCase(string Id, string Task, Dictionary<string, string> Files, string[] ExpectedChangedFiles, Dictionary<string, string> ExpectedContent, bool ReadOnly = false);
public sealed record BenchmarkResult(string Id, bool Passed, double DurationSeconds, int ToolCalls, int Retries, double QualityScore, string Detail);

public sealed class BenchmarkService
{
    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(string manifest, IModelAdapter model, CancellationToken ct)
    {
        var cases = JsonSerializer.Deserialize<BenchmarkCase[]>(await File.ReadAllTextAsync(manifest, ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var results = new List<BenchmarkResult>();
        foreach (var item in cases)
        {
            var workspace = Path.Combine(Path.GetTempPath(), "miau-benchmark-" + item.Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try
            {
                foreach (var file in item.Files) { var path = Path.Combine(workspace, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, file.Value, ct); }
                if (item.Files.Count == 0) await File.WriteAllTextAsync(Path.Combine(workspace, ".gitkeep"), "", ct);
                await RunGit(workspace, ["init", "-q"], ct); await RunGit(workspace, ["add", "."], ct); await RunGit(workspace, ["-c", "user.name=MIAU Benchmark", "-c", "user.email=benchmark@localhost", "commit", "-qm", "fixture"], ct);
                var events = new List<ExecutionEvent>(); var watch = Stopwatch.StartNew();
                var run = await new AgentOrchestrator(model, new ToolExecutor(), new BenchmarkDataset()).RunAsync(workspace, item.Task, new(!item.ReadOnly, item.ReadOnly, !item.ReadOnly), ct, eventSink: events.Add);
                var changed = (await RunGit(workspace, ["status", "--porcelain"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x[3..].Trim()).Where(x => !x.Contains("__pycache__", StringComparison.OrdinalIgnoreCase) && !x.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
                var contentOk = item.ExpectedContent.All(x => File.Exists(Path.Combine(workspace, x.Key)) && File.ReadAllText(Path.Combine(workspace, x.Key)).Contains(x.Value, StringComparison.Ordinal));
                var scopeOk = changed.SequenceEqual(item.ExpectedChangedFiles.Order());
                var passed = run.Phase == JobPhase.Completed && contentOk && scopeOk;
                var toolCalls = events.Count(x => x.Type == ExecutionEventType.ToolStarted || x.Type == ExecutionEventType.CommandStarted || x.Type == ExecutionEventType.BuildStarted || x.Type == ExecutionEventType.TestsStarted);
                results.Add(new(item.Id, passed, watch.Elapsed.TotalSeconds, toolCalls, run.Evidence.Attempts, passed ? Math.Max(0, 100 - run.Evidence.Attempts * 5) : 0, passed ? "acceptance criteria aprovados" : $"phase={run.Phase}; changed={string.Join(',', changed)}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new(item.Id, false, 0, 0, 0, 0, DatasetService.Redact(ex.Message))); }
            finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
        }
        return results;
    }
    static async Task<string> RunGit(string root, string[] args, CancellationToken ct) { var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var a in args) psi.ArgumentList.Add(a); using var p = Process.Start(psi)!; var output = await p.StandardOutput.ReadToEndAsync(ct); var error = await p.StandardError.ReadToEndAsync(ct); await p.WaitForExitAsync(ct); if (p.ExitCode != 0) throw new InvalidOperationException(error); return output; }
    sealed class BenchmarkDataset : IDatasetService { public Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct) => Task.CompletedTask; }
}
