using System.Text.RegularExpressions;

namespace Miau.Desktop;

// Compatibility facade for the UI and autonomous GitHub flow. Execution lives in the orchestrator.
public sealed class AgentService
{
    readonly ToolExecutor tools = new();
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public TimeSpan ModelTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(string root, string prompt, CancellationToken ct, Action<string> progress)
        => await RunCoreAsync(root, prompt, ct, ev => progress(ev.Description + (string.IsNullOrWhiteSpace(ev.Target) ? "" : $": {ev.Target}")));

    public Task<string> RunWithEventsAsync(string root, string prompt, CancellationToken ct, Action<ExecutionEvent> events)
        => RunCoreAsync(root, prompt, ct, events);

    async Task<string> RunCoreAsync(string root, string prompt, CancellationToken ct, Action<ExecutionEvent> events)
    {
        var requirements = Classify(prompt);
        var adapter = new OllamaModelAdapter(Model, requestTimeout: ModelTimeout);
        var orchestrator = new AgentOrchestrator(adapter, tools, new DatasetService());
        try
        {
            var result = await orchestrator.RunAsync(root, prompt, requirements, ct, eventSink: events);
            if (result.Phase != JobPhase.Completed) throw new InvalidOperationException(result.Summary);
            return result.Summary;
        }
        catch (OperationCanceledException) { throw; }
    }

    public async Task<string> GetGitStatusAsync(string root, CancellationToken ct)
    {
        var result = await tools.ExecuteAsync(root, new(ToolNames.GitStatus, [], null), true, ct);
        if (!result.Success) throw new InvalidOperationException(result.Error); return result.Output;
    }
    public async Task<string> GetGitDiffAsync(string root, CancellationToken ct)
    {
        var result = await tools.ExecuteAsync(root, new(ToolNames.GitDiff, [], null), true, ct);
        if (!result.Success) throw new InvalidOperationException(result.Error); return result.Output;
    }

    static JobRequirements Classify(string prompt)
    {
        var readOnly = Regex.IsMatch(prompt, @"\b(não|nao)\s+(altere|modifique|edite|mude)|somente\s+leitura|apenas\s+(analise|revise|explique)", RegexOptions.IgnoreCase);
        var change = !readOnly && Regex.IsMatch(prompt, @"\b(altere|modifique|edite|implemente|adicione|corrija|crie|remova|refatore|faça)\b", RegexOptions.IgnoreCase);
        return new(change, readOnly, change);
    }
}
