namespace Miau.Desktop;

public sealed record AgentRunResult(string Summary, JobPhase Phase, JobEvidence Evidence);

public sealed class AgentOrchestrator
{
    readonly IModelAdapter model;
    readonly IToolExecutor tools;
    readonly IDatasetService dataset;
    readonly int maxSteps;
    public AgentOrchestrator(IModelAdapter model, IToolExecutor tools, IDatasetService dataset, int maxSteps = 40)
    { this.model = model; this.tools = tools; this.dataset = dataset; this.maxSteps = maxSteps; }

    public async Task<AgentRunResult> RunAsync(string workspace, string task, JobRequirements requirements, CancellationToken ct, Action<JobPhase, string>? progress = null)
    {
        var engine = new JobEngine(requirements); if (progress is not null) engine.StateChanged += progress;
        var turns = new List<ModelTurn> { new("user", task) }; var plan = new List<string>(); var events = new List<TaskTraceEvent>();
        engine.Start(); engine.BeginInspection();
        var initial = await tools.ExecuteAsync(workspace, new(ToolNames.ListFiles, new() { ["path"] = "." }, "Inspeção inicial determinada pelo motor."), true, ct);
        engine.Observe(initial); Record(events, initial);
        if (!initial.Success) { engine.Fail(initial.Error!); return new(initial.Error!, engine.Phase, engine.Evidence); }

        for (var step = 0; step < maxSteps && !engine.IsTerminal; step++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await model.CompleteStepAsync(new(SystemPrompt(workspace, requirements), turns), ct);
            if (!BrainResponse.TryParse(raw, out var response, out var parseError))
            {
                events.Add(new(DateTimeOffset.Now, "protocol_error", model.ModelId, false, parseError));
                if (!engine.RecordFailure(parseError)) break;
                turns.Add(new("assistant", raw)); turns.Add(new("user", $"PROTOCOLO INVÁLIDO: {parseError} Retorne somente um objeto JSON válido do protocolo MIAU.")); continue;
            }
            if (response!.Type == "plan")
            {
                plan.Clear(); plan.AddRange(response.Plan!.Steps); engine.BeginPlanning();
                events.Add(new(DateTimeOffset.Now, "plan", model.ModelId, true, string.Join(" | ", plan)));
                turns.Add(new("assistant", raw)); turns.Add(new("user", "Plano registrado. Emita a próxima action estruturada.")); continue;
            }
            if (response.Type == "action")
            {
                events.Add(new(DateTimeOffset.Now, "action_requested", response.Action!.Action, true,
                    string.Join("; ", response.Action.Arguments.Select(x => $"{x.Key}={x.Value}"))));
                var result = await tools.ExecuteAsync(workspace, response.Action!, requirements.ReadOnly, ct); engine.Observe(result); Record(events, result);
                turns.Add(new("assistant", raw)); turns.Add(new("user", ToolObservation(result)));
                if (!result.Success && !engine.RecordFailure(result.Error!)) break;
                continue;
            }

            var final = response.Final!;
            if (requirements.RequiresChange)
            {
                var diff = await tools.ExecuteAsync(workspace, new(ToolNames.GitDiff, [], "Verificação obrigatória do motor."), true, ct); engine.Observe(diff); Record(events, diff);
                if (diff.Success && engine.Evidence.HasGitDiff)
                {
                    var validation = await tools.ValidateAsync(workspace, ct); engine.Observe(validation); Record(events, validation);
                    if (!validation.Success)
                    {
                        if (!engine.RecordFailure(validation.Error!)) break;
                        turns.Add(new("assistant", raw)); turns.Add(new("user", ToolObservation(validation) + "\nCorrija o erro com uma action estruturada; não finalize ainda.")); continue;
                    }
                }
            }
            if (!engine.TryComplete(out var reason))
            {
                if (!engine.RecordFailure(reason)) break;
                turns.Add(new("assistant", raw)); turns.Add(new("user", $"FINAL RECUSADO PELO JOB ENGINE: {reason} Emita a action estruturada necessária.")); continue;
            }
            var record = new TrainingRecord(task, Path.GetFileName(workspace), plan, engine.Evidence, events, final.Summary);
            await dataset.SaveCompletedAsync(workspace, record, ct);
            return new(final.Summary, engine.Phase, engine.Evidence);
        }
        if (!engine.IsTerminal) engine.Fail($"Limite de {maxSteps} etapas atingido.");
        return new(engine.LastError ?? "A tarefa falhou.", engine.Phase, engine.Evidence);
    }

    static void Record(List<TaskTraceEvent> events, ToolResult result) => events.Add(new(DateTimeOffset.Now, "tool", result.Tool, result.Success, result.Success ? result.Output : result.Error ?? "erro"));
    static string ToolObservation(ToolResult result) => $"RESULTADO ESTRUTURADO DA FERRAMENTA {result.Tool}: success={result.Success}; output={Trim(result.Output)}; error={result.Error ?? ""}";
    static string Trim(string value) => value.Length > 30000 ? value[..30000] + "\n[truncado]" : value;
    static string SystemPrompt(string workspace, JobRequirements requirements) => $$"""
Você é o cérebro de programação do MIAU, operando em {{workspace}}. O sistema, não você, controla execução e conclusão.
Responda SOMENTE com um objeto JSON, sem markdown ou prosa externa. Formatos permitidos:
{"type":"plan","steps":["..."]}
{"type":"action","action":"read_file","arguments":{"path":"..."},"reason":"..."}
{"type":"final","summary":"...","files_changed":["..."]}
Ações: list_files, read_file, search, write_file, replace_in_file, apply_patch, git_status, git_diff, build, test, run_command.
Inspecione arquivos relevantes antes de editar. Não trate intenção textual como ação. Não faça commit ou push.
Tarefa somente leitura: {{requirements.ReadOnly}}. Exige alteração: {{requirements.RequiresChange}}.
""";
}
