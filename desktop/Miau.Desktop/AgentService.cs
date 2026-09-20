using System.Text.RegularExpressions;

namespace Miau.Desktop;

// Compatibility facade for the UI and autonomous GitHub flow. Execution lives in the orchestrator.
public sealed class AgentService
{
    readonly ToolExecutor tools = new();
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public TimeSpan ModelTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public AgentRunResult? LastRunResult { get; private set; }

    public async Task<string> RunAsync(string root, string prompt, CancellationToken ct, Action<string> progress, string origin = "interactive")
        => await RunCoreAsync(root, prompt, ct, ev => progress(ev.Description + (string.IsNullOrWhiteSpace(ev.Target) ? "" : $": {ev.Target}")), origin);

    public Task<string> RunWithEventsAsync(string root, string prompt, CancellationToken ct, Action<ExecutionEvent> events)
        => RunCoreAsync(root, prompt, ct, events, "interactive");

    async Task<string> RunCoreAsync(string root, string prompt, CancellationToken ct, Action<ExecutionEvent> events, string origin)
    {
        var requirements = Classify(prompt);
        var adapter = new OllamaModelAdapter(Model, requestTimeout: ModelTimeout);
        var orchestrator = new AgentOrchestrator(adapter, tools, new DatasetService());
        try
        {
            var result = await orchestrator.RunAsync(root, prompt, requirements, ct, eventSink: events, origin: origin); LastRunResult = result;
            if (result.Phase != JobPhase.Completed) throw new InvalidOperationException(result.Summary);
            var evidence = result.Evidence;
            var files = evidence.FilesChanged.Count == 0 ? "nenhum" : string.Join(", ", evidence.FilesChanged);
            return $"{result.Summary}\n\nEvidências do JobEngine:\n- Arquivos alterados: {files}\n- Git diff: {(evidence.HasGitDiff ? "validado" : "não aplicável")}\n- Validação: {(evidence.ValidationPassed ? "aprovada" : requirements.RequiresValidation ? "ausente" : "não aplicável")}\n- Retries: {evidence.Attempts}";
        }
        catch (OperationCanceledException) { throw; }
    }

    public async Task<IReadOnlyList<BenchmarkResult>> RunBenchmarkAsync(string manifest, CancellationToken ct)
    {
        var adapter = new OllamaModelAdapter(Model, requestTimeout: ModelTimeout);
        return await new BenchmarkService().RunAsync(manifest, adapter, ct);
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
        // Classify explicit requested work first. A scope guard such as
        // "não altere nenhum outro arquivo" must not turn a create/edit task into read-only.
        var explicitReadOnly = Regex.IsMatch(prompt, @"\b(somente\s+leitura|apenas\s+(analise|revise|explique)|não\s+(altere|modifique|edite|mude)\s+(nada|nenhum\s+arquivo|qualquer\s+arquivo|o\s+projeto))\b", RegexOptions.IgnoreCase);
        var positiveText = Regex.Replace(prompt, @"\bnão\s+(altere|modifique|edite|mude)\b[^.!?]*(?:[.!?]|$)", "", RegexOptions.IgnoreCase);
        var change = Regex.IsMatch(positiveText, @"\b(altere|modifique|edite|implemente|adicione|corrija|crie|remova|refatore|faça)\b", RegexOptions.IgnoreCase);
        var readOnly = !change && explicitReadOnly;
        var visual = change && Regex.IsMatch(prompt, @"\b(site|página|pagina|layout|interface|ui|ux|visual|responsiv|html|css|frontend|front-end|tela|design)\b", RegexOptions.IgnoreCase);
        return new(change, readOnly, change, visual);
    }
}
