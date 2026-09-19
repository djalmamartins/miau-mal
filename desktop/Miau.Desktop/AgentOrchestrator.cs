using System.Diagnostics;

namespace Miau.Desktop;

public sealed record AgentRunResult(string Summary, JobPhase Phase, JobEvidence Evidence);

public sealed class AgentOrchestrator
{
    readonly IModelAdapter model; readonly IToolExecutor tools; readonly IDatasetService dataset; readonly IPromptProvider prompts; readonly EditPolicy editPolicy; readonly FailureLearningService failures; readonly ExperienceHarvester harvester; readonly int maxSteps;
    public AgentOrchestrator(IModelAdapter model, IToolExecutor tools, IDatasetService dataset, int maxSteps = 40, IPromptProvider? prompts = null)
    { this.model = model; this.tools = tools; this.dataset = dataset; this.maxSteps = maxSteps; this.prompts = prompts ?? new VersionedPromptProvider(); editPolicy = new(); failures = new(); harvester = new(); }

    public async Task<AgentRunResult> RunAsync(string workspace, string task, JobRequirements requirements, CancellationToken ct,
        Action<JobPhase, string>? progress = null, Action<ExecutionEvent>? eventSink = null)
    {
        var engine = new JobEngine(requirements); var trace = new List<TaskTraceEvent>(); var plan = new List<string>(); var replaceFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); string? lastResponseKey = null; string? lastProgressKey = null; var consecutiveSameResponse = 0; var turns = new List<ModelTurn> { new("user", task) }; var jobWatch = Stopwatch.StartNew();
        (MiauAction Action, ToolResult Result)? pendingRecovery = null;
        void Emit(ExecutionEventType type, string description, string? target = null, TimeSpan? duration = null, bool? success = null, IReadOnlyDictionary<string, string>? metadata = null, string? details = null)
            => eventSink?.Invoke(new(DateTimeOffset.Now, type, engine.Phase, description, target, duration, success, metadata, details));
        engine.StateChanged += (phase, description) => { progress?.Invoke(phase, description); Emit(ExecutionEventType.PhaseChanged, description, phase.ToString(), success: true); };
        Emit(ExecutionEventType.JobStarted, "Tarefa recebida", Miau1Coder.AgentName);
        try
        {
            engine.Start(); engine.BeginInspection();
            var initial = await ExecuteTool(workspace, new(ToolNames.ListFiles, new() { ["path"] = "." }, "Inspeção inicial determinada pelo motor."), true, engine, trace, Emit, ct);
            if (!initial.Success) { engine.Fail(initial.Error!); Emit(ExecutionEventType.JobFailed, initial.Error!, success: false); return new(initial.Error!, engine.Phase, engine.Evidence); }
            turns.Add(new("user", "INSPEÇÃO INICIAL EXECUTADA PELO SISTEMA:\n" + Trim(initial.Output) + "\nUse somente caminhos reais desta listagem e inspecione o arquivo relevante antes de editar."));

            for (var step = 0; step < maxSteps && !engine.IsTerminal; step++)
            {
                ct.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew();
                Emit(ExecutionEventType.ModelRequestStarted, "MIAU1-Coder processando", model.ModelId);
                string raw;
                try { raw = await model.CompleteStepAsync(new(prompts.GetSystemPrompt(workspace, requirements), turns), ct); }
                catch (TimeoutException ex) { engine.Fail(ex.Message); Emit(ExecutionEventType.JobFailed, "Ollama sem resposta", model.ModelId, watch.Elapsed, false, details: ex.Message); break; }
                Emit(ExecutionEventType.ModelRequestCompleted, "Resposta estruturada recebida", model.ModelId, watch.Elapsed, true);
                var responseKey = raw.Trim();
                var progressKey = ProgressKey(engine.Evidence);
                if (responseKey == lastResponseKey && progressKey == lastProgressKey) consecutiveSameResponse++;
                else consecutiveSameResponse = 1;
                lastResponseKey = responseKey; lastProgressKey = progressKey;
                if (consecutiveSameResponse >= 3)
                {
                    var loop = "Loop detectado: o modelo repetiu a mesma resposta estruturada 3 vezes sem nova evidência.";
                    engine.Fail(loop);
                    Emit(ExecutionEventType.JobFailed, loop, model.ModelId, success: false);
                    break;
                }

                if (!BrainResponse.TryParse(raw, out var response, out var parseError))
                {
                    trace.Add(new(DateTimeOffset.Now, "protocol_error", model.ModelId, false, parseError));
                    if (!engine.RecordFailure(parseError)) break;
                    Emit(ExecutionEventType.RetryStarted, "Resposta inválida; solicitando protocolo MIAU", model.ModelId, success: false, details: parseError);
                    turns.Add(new("assistant", raw)); turns.Add(new("user", $"PROTOCOLO INVÁLIDO: {parseError} Retorne somente um objeto JSON válido do protocolo MIAU.")); continue;
                }
                if (response!.Type == "plan")
                {
                    plan.Clear(); plan.AddRange(response.Plan!.Steps); engine.BeginPlanning();
                    trace.Add(new(DateTimeOffset.Now, "plan", model.ModelId, true, string.Join(" | ", plan)));
                    turns.Add(new("assistant", raw)); turns.Add(new("user", "Plano registrado. Emita a próxima action estruturada.")); continue;
                }
                if (response.Type == "action")
                {
                    trace.Add(new(DateTimeOffset.Now, "action_requested", response.Action!.Action, true, string.Join("; ", response.Action.Arguments.Select(x => $"{x.Key}={x.Value}"))));
                    if (!editPolicy.Allow(response.Action))
                    {
                        var blocked = ToolResult.Fail(response.Action.Action, "replace_in_file bloqueado após falhas repetidas; releia o arquivo e use write_file ou apply_patch.");
                        trace.Add(new(DateTimeOffset.Now, "tool", blocked.Tool, false, blocked.Error!)); engine.RecordFailure(blocked.Error!);
                        turns.Add(new("assistant", raw)); turns.Add(new("user", ToolObservation(blocked))); Emit(ExecutionEventType.RetryStarted, "Mudando estratégia de edição", Target(response.Action), success: false, details: blocked.Error); continue;
                    }
                    var result = await ExecuteTool(workspace, response.Action!, requirements.ReadOnly, engine, trace, Emit, ct);
                    editPolicy.Observe(response.Action, result);
                    if (!result.Success)
                    {
                        await failures.RecordAsync(workspace, response.Action, result, null, false, ct);
                        pendingRecovery = (response.Action, result);
                    }
                    else if (pendingRecovery is { } recovery)
                    {
                        await failures.RecordAsync(workspace, recovery.Action, recovery.Result, response.Action.Action, true, ct);
                        pendingRecovery = null;
                    }
                    turns.Add(new("assistant", raw)); turns.Add(new("user", ToolObservation(result)));
                    if (!result.Success)
                    {
                        if (!engine.RecordFailure(result.Error!)) break;
                        Emit(ExecutionEventType.RetryStarted, "Corrigindo falha da ferramenta", result.Tool, success: false, details: result.Error);
                        if (result.Tool == ToolNames.ReplaceInFile)
                        {
                            var target = Target(response.Action!) ?? "(arquivo)";
                            replaceFailures[target] = replaceFailures.GetValueOrDefault(target) + 1;
                            if (replaceFailures[target] >= 2)
                                turns.Add(new("user", $"RECUPERAÇÃO DETERMINÍSTICA: replace_in_file falhou {replaceFailures[target]} vezes em {target}. Não use replace_in_file novamente neste arquivo nesta tarefa. Leia o arquivo atual e use write_file para gravar o conteúdo completo desejado."));
                            else
                                turns.Add(new("user", "A edição pontual falhou. Não repita a mesma substituição. Leia novamente o arquivo para obter o conteúdo atual. Se a alteração for ampla, prefira write_file com o conteúdo completo e correto do arquivo; se for pequena, use um old_text maior que seja único."));
                        }
                    }
                    continue;
                }

                var final = response.Final!;
                if (requirements.RequiresChange)
                {
                    var diff = await ExecuteTool(workspace, new(ToolNames.GitDiff, [], "Verificação obrigatória do motor."), true, engine, trace, Emit, ct);
                    if (diff.Success && engine.Evidence.HasGitDiff)
                    {
                        Emit(ExecutionEventType.BuildStarted, "Executando validação automática", target: "build/test"); var validationWatch = Stopwatch.StartNew();
                        var validation = await tools.ValidateAsync(workspace, ct); engine.Observe(validation); Record(trace, validation);
                        Emit(validation.Success ? ExecutionEventType.BuildCompleted : ExecutionEventType.BuildFailed,
                            validation.Success ? "Build/teste aprovado" : "Build/teste falhou", validation.Tool, validationWatch.Elapsed, validation.Success, validation.Metadata, Details(validation));
                        if (!validation.Success)
                        {
                            if (!engine.RecordFailure(validation.Error!)) break;
                            Emit(ExecutionEventType.RetryStarted, "Enviando erro ao MIAU1-Coder", validation.Tool, success: false, details: validation.Error);
                            turns.Add(new("assistant", raw)); turns.Add(new("user", ToolObservation(validation) + "\nCorrija o erro com uma action estruturada; não finalize ainda.")); continue;
                        }
                    }
                }
                if (requirements.RequiresChange)
                {
                    var actual = engine.Evidence.FilesChanged.Where(x => x != "(patch)").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    var declared = final.FilesChanged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (actual.Length == 0)
                    {
                        const string scopeReason = "Nenhuma alteração comprovada pelo JobEngine.";
                        if (!engine.RecordFailure(scopeReason)) break;
                        Emit(ExecutionEventType.RetryStarted, "Escopo da alteração recusado", success: false, details: scopeReason);
                        turns.Add(new("assistant", raw)); turns.Add(new("user", $"SCOPE GUARD: {scopeReason} Faça a alteração solicitada antes de finalizar.")); continue;
                    }
                    if (declared.Length == 0)
                    {
                        // The model's prose declaration is advisory. The deterministic JobEngine already
                        // knows the files actually changed, so do not burn a retry for an empty files_changed.
                        final = final with { FilesChanged = actual };
                        Emit(ExecutionEventType.ToolCompleted, "Escopo final reconciliado com evidência do JobEngine", string.Join(", ", actual), success: true);
                    }
                    else
                    {
                        var unexpected = actual.Except(declared, StringComparer.OrdinalIgnoreCase).ToArray();
                        if (unexpected.Length > 0)
                        {
                            var scopeReason = "Arquivos fora do escopo declarado: " + string.Join(", ", unexpected);
                            if (!engine.RecordFailure(scopeReason)) break;
                            Emit(ExecutionEventType.RetryStarted, "Escopo da alteração recusado", success: false, details: scopeReason);
                            turns.Add(new("assistant", raw)); turns.Add(new("user", $"SCOPE GUARD: {scopeReason} Declare exatamente os arquivos alterados: {string.Join(", ", actual)}.")); continue;
                        }
                    }
                }
                if (!engine.TryComplete(out var reason))
                {
                    if (!engine.RecordFailure(reason)) break;
                    Emit(ExecutionEventType.RetryStarted, "Conclusão recusada pelo JobEngine", success: false, details: reason);
                    turns.Add(new("assistant", raw)); turns.Add(new("user", $"FINAL RECUSADO PELO JOB ENGINE: {reason} Emita a action estruturada necessária.")); continue;
                }
                var evidence = engine.Evidence;
                var provenFiles = evidence.FilesChanged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                var finalSummary = BuildVerifiedSummary(final.Summary, provenFiles, evidence);
                var record = harvester.Harvest(workspace, model.ModelId, task, plan, trace, evidence, finalSummary, jobWatch.Elapsed);
                await dataset.SaveCompletedAsync(workspace, record, ct);
                Emit(ExecutionEventType.JobCompleted, "Tarefa concluída", duration: null, success: true, metadata: new Dictionary<string, string> { ["files_changed"] = evidence.FilesChanged.Count.ToString() });
                return new(finalSummary, engine.Phase, evidence);
            }
            if (!engine.IsTerminal) engine.Fail($"Limite de {maxSteps} etapas atingido.");
            Emit(ExecutionEventType.JobFailed, engine.LastError ?? "A tarefa falhou.", success: false);
            if (dataset is DatasetService concreteDataset) await concreteDataset.SaveRejectedAsync(task, model.ModelId, engine.LastError ?? "falha", ct);
            return new(engine.LastError ?? "A tarefa falhou.", engine.Phase, engine.Evidence);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { engine.Cancel(); Emit(ExecutionEventType.JobCancelled, "Tarefa cancelada pelo usuário", success: false); throw; }
    }

    delegate void EventEmitter(ExecutionEventType type, string description, string? target = null, TimeSpan? duration = null, bool? success = null, IReadOnlyDictionary<string, string>? metadata = null, string? details = null);
    async Task<ToolResult> ExecuteTool(string workspace, MiauAction action, bool readOnly, JobEngine engine, List<TaskTraceEvent> trace, EventEmitter emit, CancellationToken ct)
    {
        var (started, completed, failed) = EventTypes(action.Action); var target = Target(action); var watch = Stopwatch.StartNew();
        emit(started, Description(action.Action, true), target, details: string.Join("\n", action.Arguments.Select(x => $"{x.Key}: {x.Value}")));
        var result = await tools.ExecuteAsync(workspace, action, readOnly, ct); engine.Observe(result); Record(trace, result);
        emit(result.Success ? completed : failed, Description(action.Action, result.Success), target, watch.Elapsed, result.Success, result.Metadata, Details(result));
        return result;
    }
    static (ExecutionEventType, ExecutionEventType, ExecutionEventType) EventTypes(string tool) => tool switch
    {
        ToolNames.GitDiff => (ExecutionEventType.DiffStarted, ExecutionEventType.DiffCompleted, ExecutionEventType.ToolFailed),
        ToolNames.Build => (ExecutionEventType.BuildStarted, ExecutionEventType.BuildCompleted, ExecutionEventType.BuildFailed),
        ToolNames.Test => (ExecutionEventType.TestsStarted, ExecutionEventType.TestsCompleted, ExecutionEventType.TestsFailed),
        ToolNames.RunCommand => (ExecutionEventType.CommandStarted, ExecutionEventType.CommandCompleted, ExecutionEventType.CommandFailed),
        ToolNames.RenderPage => (ExecutionEventType.ToolStarted, ExecutionEventType.ToolCompleted, ExecutionEventType.ToolFailed),
        _ => (ExecutionEventType.ToolStarted, ExecutionEventType.ToolCompleted, ExecutionEventType.ToolFailed)
    };
    static string Description(string tool, bool success) => success ? tool switch
    { ToolNames.ReadFile => "Leu arquivo", ToolNames.WriteFile => "Criou arquivo", ToolNames.ReplaceInFile or ToolNames.ApplyPatch => "Alterou arquivo", ToolNames.GitDiff => "Diff validado", ToolNames.FetchUrl => "Acessou referência web", ToolNames.RenderPage => "Renderizou e capturou a interface", ToolNames.InspectVisual => "Analisou visualmente o screenshot", _ => $"Executou {tool}" }
        : $"Falha em {tool}";
    static string? Target(MiauAction action) => action.Arguments.TryGetValue("path", out var path) ? path : action.Arguments.TryGetValue("command", out var command) ? command : action.Arguments.TryGetValue("url", out var url) ? url : action.Action;
    static string Details(ToolResult result) => result.Success ? Trim(result.Output) : result.Error ?? "Erro";
    static void Record(List<TaskTraceEvent> events, ToolResult result) => events.Add(new(DateTimeOffset.Now, "tool", result.Tool, result.Success, result.Success ? result.Output : result.Error ?? "erro"));
    static string BuildVerifiedSummary(string modelSummary, IReadOnlyList<string> files, JobEvidence evidence)
    {
        var summary = string.IsNullOrWhiteSpace(modelSummary) ? "Tarefa concluída." : modelSummary.Trim();
        if (files.Count > 0)
            summary += "\n\nArquivos alterados: " + string.Join(", ", files.Select(x => "[" + x + "]"));
        if (evidence.HasGitDiff)
            summary += "\nVerificação: alterações confirmadas pelo Git diff.";
        if (evidence.ValidationRan && evidence.ValidationPassed)
            summary += "\nValidação: concluída com sucesso.";
        if (evidence.VisualValidationRan)
            summary += "\nValidação visual: página renderizada e screenshot gerado.";
        if (evidence.VisualInspectionPassed)
            summary += "\nInspeção visual: aprovada pelo modelo visual local.";
        return summary;
    }

    static string ProgressKey(JobEvidence evidence) =>
        $"{evidence.FilesInspected.Count}|{evidence.FilesChanged.Count}|{evidence.HasGitDiff}|{evidence.ValidationRan}|{evidence.VisualValidationRan}|{evidence.VisualInspectionRan}|{evidence.VisualInspectionPassed}|{evidence.Attempts}";

    static string ToolObservation(ToolResult result) => $"RESULTADO ESTRUTURADO DA FERRAMENTA {result.Tool}: success={result.Success}; output={Trim(result.Output)}; error={result.Error ?? ""}";
    static string Trim(string value) => value.Length > 30000 ? value[..30000] + "\n[truncado]" : value;
}
