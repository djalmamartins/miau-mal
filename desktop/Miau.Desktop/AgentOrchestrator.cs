using System.Diagnostics;

namespace Miau.Desktop;

public sealed record AgentRunMetrics(int NoEffectiveChangeCount = 0, int RecoveryCount = 0, int EffectiveChanges = 0, bool HumanIntervention = false, int Actions = 0);
public sealed record AgentRunResult(string Summary, JobPhase Phase, JobEvidence Evidence, string? TrainingRecordId = null, AgentRunMetrics? Metrics = null);

public sealed class AgentOrchestrator
{
    readonly IModelAdapter model; readonly IToolExecutor tools; readonly IDatasetService dataset; readonly IPromptProvider prompts; readonly EditPolicy editPolicy; readonly FailureLearningService failures; readonly RecoveryEpisodeService recoveryEpisodes; readonly ExperienceHarvester harvester; readonly int maxSteps;
    public AgentOrchestrator(IModelAdapter model, IToolExecutor tools, IDatasetService dataset, int maxSteps = 40, IPromptProvider? prompts = null)
    { this.model = model; this.tools = tools; this.dataset = dataset; this.maxSteps = maxSteps; this.prompts = prompts ?? new VersionedPromptProvider(); editPolicy = new(); failures = new(); recoveryEpisodes = new(); harvester = new(); }

    public async Task<AgentRunResult> RunAsync(string workspace, string task, JobRequirements requirements, CancellationToken ct,
        Action<JobPhase, string>? progress = null, Action<ExecutionEvent>? eventSink = null, string origin = "interactive")
    {
        var acceptance = AcceptancePlanner.Build(task, requirements); var engine = new JobEngine(requirements, acceptance: acceptance); var tracker = new ProgressTracker(); var recoveryEngine = new RecoveryEngine(); var visualRevisions = new VisualRevisionPolicy();
        var workspaceBaseline = Directory.Exists(workspace) ? WorkspaceBaseline.Capture(workspace) : null;
        var trace = new List<TaskTraceEvent>(); var plan = new List<string>(); var replaceFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var consecutiveTimeouts = 0; var turns = new List<ModelTurn> { new("user", task) }; var jobWatch = Stopwatch.StartNew();
        (MiauAction Action, ToolResult Result)? pendingRecovery = null;
        (MiauAction Action, RecoveryDecision Decision)? pendingIneffectiveRecovery = null;
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
                try { raw = await model.CompleteStepAsync(new(prompts.GetSystemPrompt(workspace, requirements), turns), ct); consecutiveTimeouts = 0; }
                catch (TimeoutException ex)
                {
                    consecutiveTimeouts++; Emit(ExecutionEventType.ModelTimeout, $"Timeout {consecutiveTimeouts}/2 — retomando", model.ModelId, watch.Elapsed, false, details: ex.Message);
                    if (consecutiveTimeouts >= 2) { engine.Fail("O modelo excedeu o tempo limite duas vezes consecutivas; estado da tarefa preservado."); break; }
                    turns = CompactContext(turns, plan, engine.Evidence, acceptance); continue;
                }
                Emit(ExecutionEventType.ModelRequestCompleted, "Resposta estruturada recebida", model.ModelId, watch.Elapsed, true);

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
                    var mutatingAction = response.Action!.Action is ToolNames.WriteFile or ToolNames.ReplaceInFile or ToolNames.ApplyPatch;
                    var stagnation = mutatingAction ? new StagnationResult(false, 0, "", "") : tracker.ObserveAction(response.Action!);
                    if (stagnation.Detected)
                    {
                        Emit(ExecutionEventType.StagnationDetected, stagnation.Message, stagnation.Cycle, success: false, metadata: new Dictionary<string,string> { ["level"] = stagnation.Level.ToString() });
                        if (stagnation.Level >= 4) { engine.Fail("Estagnação persistente após recuperação progressiva: " + stagnation.Cycle); break; }
                        var instruction = stagnation.Level switch { 1 => "Não repita o ciclo; escolha uma ação que gere nova evidência.", 2 => "A estratégia repetida está temporariamente bloqueada. Use outra ferramenta ou arquivo relevante.", _ => "Replaneje agora usando critérios pendentes e evidências atuais; responda com type=plan." };
                        Emit(ExecutionEventType.RecoveryStarted, $"Recuperação de estagnação nível {stagnation.Level}", success: true, details: instruction);
                        turns.Add(new("assistant", raw)); turns.Add(new("user", $"ESTAGNAÇÃO DETECTADA\nCiclo: {stagnation.Cycle}\n{instruction}\nCritérios pendentes: {acceptance.PendingSummary()}")); continue;
                    }
                    trace.Add(new(DateTimeOffset.Now, "action_requested", response.Action!.Action, true, string.Join("; ", response.Action.Arguments.Select(x => $"{x.Key}={x.Value}"))));

                    // Broad UI/site rewrites should not degrade into a long sequence of tiny replacements.
                    // The orchestrator owns this policy; the model only proposes actions.
                    if (response.Action.Action == ToolNames.ReplaceInFile && requirements.RequiresVisualValidation)
                    {
                        var targetPath = Target(response.Action) ?? "";
                        var broadVisualTask = IsBroadVisualRewrite(task);
                        var decision = editPolicy.Choose(targetPath, fileExists: true, fileRead: engine.Evidence.FilesInspected.Contains(targetPath, StringComparer.OrdinalIgnoreCase),
                            changedRegions: broadVisualTask ? 2 : 1, fileCount: 1, substantialRewrite: broadVisualTask);
                        if (decision.Method != EditMethod.Replace)
                        {
                            var blocked = ToolResult.Fail(response.Action.Action, $"replace_in_file inadequado para esta tarefa: {decision.Reason}.");
                            trace.Add(new(DateTimeOffset.Now, "policy", blocked.Tool, false, blocked.Error!));
                            turns.Add(new("assistant", raw));
                            turns.Add(new("user", ToolObservation(blocked) + "\nESTRATÉGIA OBRIGATÓRIA: a tarefa pede uma reformulação visual ampla. Use write_file para reestruturar integralmente o arquivo lido ou apply_patch para múltiplas alterações estruturadas. Não reduza o pedido a trocas pontuais de texto."));
                            Emit(ExecutionEventType.PolicyRecovery, "Selecionando estratégia de edição estrutural", targetPath, success: true, details: blocked.Error);
                            continue;
                        }
                    }
                    if (!editPolicy.Allow(response.Action))
                    {
                        var blocked = ToolResult.Fail(response.Action.Action, "replace_in_file bloqueado após falhas repetidas; releia o arquivo e use write_file ou apply_patch.");
                        trace.Add(new(DateTimeOffset.Now, "policy", blocked.Tool, false, blocked.Error!));
                        turns.Add(new("assistant", raw));
                        turns.Add(new("user", ToolObservation(blocked) + "\nRECUPERAÇÃO OBRIGATÓRIA: não tente replace_in_file novamente. Use write_file com o conteúdo completo atual ou apply_patch."));
                        Emit(ExecutionEventType.PolicyRecovery, "Mudando estratégia de edição", Target(response.Action), success: true, details: blocked.Error); continue;
                    }
                    if (response.Action.Action == ToolNames.RenderPage && requirements.RequiresVisualValidation && IsBroadVisualRewrite(task))
                    {
                        var htmlPath = Target(response.Action) ?? "";
                        var cssPath = Path.Combine(Path.GetDirectoryName(htmlPath) ?? "", "style.css").Replace('\\', '/');
                        var htmlCheck = await tools.ExecuteAsync(workspace, new(ToolNames.ReadFile, new() { ["path"] = htmlPath }, "Verificação determinística dos critérios visuais."), true, ct);
                        var cssCheck = await tools.ExecuteAsync(workspace, new(ToolNames.ReadFile, new() { ["path"] = cssPath }, "Verificação determinística da responsividade."), true, ct);
                        var visualAcceptance = VisualAcceptance.Evaluate(task, htmlCheck.Success ? htmlCheck.Output : "", cssCheck.Success ? cssCheck.Output : "");
                        if (!visualAcceptance.Passed)
                        {
                            var blocked = ToolResult.Fail(ToolNames.RenderPage, "Critérios de aceitação ainda não atendidos: " + string.Join(", ", visualAcceptance.Missing));
                            trace.Add(new(DateTimeOffset.Now, "acceptance", ToolNames.RenderPage, false, blocked.Error!));
                            turns.Add(new("assistant", raw));
                            turns.Add(new("user", $"RENDERIZAÇÃO BLOQUEADA: {blocked.Error}. Implemente essas entregas concretas antes de renderizar ou inspecionar visualmente."));
                            Emit(ExecutionEventType.PolicyRecovery, "Critérios de aceitação pendentes", htmlPath, success: true, details: blocked.Error);
                            continue;
                        }
                        var producedByTask = workspaceBaseline?.ChangesProducedNow(workspace).Any(x => x.Equals(htmlPath, StringComparison.OrdinalIgnoreCase) || x.Equals(cssPath, StringComparison.OrdinalIgnoreCase)) == true;
                        foreach (var criterion in acceptance.Criteria.Where(x => x.Type is AcceptanceType.Structural or AcceptanceType.Modification))
                            if (acceptance.Satisfy(criterion.Id, $"Estrutura comprovada em task delta: {htmlPath}", producedByTask))
                            {
                                tracker.Record(ProgressKind.Criterion, criterion.Id);
                                Emit(ExecutionEventType.ProgressRecorded, $"Novo critério satisfeito: {criterion.Id}", htmlPath, success: true);
                            }
                    }
                    var result = await ExecuteTool(workspace, response.Action!, requirements.ReadOnly, engine, trace, Emit, ct);
                    var outcomeCriterion = acceptance.Criteria.FirstOrDefault(x => x.Required && x.Status == AcceptanceStatus.Pending)?.Id ?? "task-change";
                    tracker.ObserveResult(response.Action, result, outcomeCriterion);
                    if (result.Success && response.Action.Action == ToolNames.InspectVisual && result.Metadata.GetValueOrDefault("visual_verdict") == "review")
                    {
                        var screenshot = response.Action.Arguments.GetValueOrDefault("screenshot_path", "");
                        var hash = File.Exists(screenshot) ? ProgressTracker.ArtifactHash(await File.ReadAllBytesAsync(screenshot, ct)) : "missing";
                        var criteriaState = string.Join('|', acceptance.Criteria.Select(x => $"{x.Id}:{x.Status}"));
                        if (!visualRevisions.CanRevise(hash, criteriaState, out var revisionReason)) { engine.Fail(revisionReason); Emit(ExecutionEventType.JobFailed, revisionReason, success: false); break; }
                    }
                    if (RecordProgress(tracker, response.Action, result, acceptance)) Emit(ExecutionEventType.ProgressRecorded, "Nova evidência de progresso", response.Action.Action, success: true, metadata: new Dictionary<string,string> { ["progress_version"] = tracker.Snapshot.Version.ToString() });
                    editPolicy.Observe(response.Action, result);
                    if (result.Success && result.Metadata.GetValueOrDefault("effective_change") == "false")
                    {
                        var decision = recoveryEngine.ObserveNoEffectiveChange(task, response.Action, result, acceptance); pendingIneffectiveRecovery = (response.Action, decision);
                        trace.Add(new(DateTimeOffset.Now, "recovery", "no_effective_change", false, decision.Context.ToPrompt()));
                        Emit(ExecutionEventType.RecoveryStarted, $"Recovery NoEffectiveChange nível {decision.Level}", Target(response.Action), success: true,
                            metadata: new Dictionary<string, string> { ["recovery_level"] = decision.Level.ToString(), ["no_effective_change"] = "true" }, details: decision.Message);
                        if (decision.Stop)
                        {
                            var diagnostic = recoveryEngine.Diagnostic(task, acceptance); engine.Fail(diagnostic);
                            await recoveryEpisodes.RecordAsync(workspace, task, response.Action, decision, null, "stopped", ct); break;
                        }
                        if (decision.Level >= 2) turns = [new("user", decision.Context.ToPrompt())];
                        else { turns.Add(new("assistant", raw)); turns.Add(new("user", decision.Message)); }
                        continue;
                    }
                    if (recoveryEngine.RecordEffectiveProgress(response.Action, result) && pendingIneffectiveRecovery is { } ineffective)
                    {
                        await recoveryEpisodes.RecordAsync(workspace, task, ineffective.Action, ineffective.Decision, response.Action.Action, "recovered", ct);
                        trace.Add(new(DateTimeOffset.Now, "recovery_success", response.Action.Action, true, "Progresso efetivo após NoEffectiveChange."));
                        Emit(ExecutionEventType.ProgressRecorded, "Recovery produziu alteração efetiva", Target(response.Action), success: true, metadata: new Dictionary<string,string> { ["recovery_success"] = "true" });
                        pendingIneffectiveRecovery = null;
                    }
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
                                turns.Add(new("user", "RECUPERAÇÃO OBRIGATÓRIA: esta substituição falhou. Não repita o mesmo old_text. Leia novamente o arquivo e, na próxima ação de edição deste arquivo, use write_file com o conteúdo completo correto ou apply_patch. Só volte a replace_in_file se o old_text for diferente, maior e comprovadamente único."));
                        }
                    }
                    continue;
                }

                var final = response.Final!;
                Emit(ExecutionEventType.CriteriaUpdated, $"Critérios {acceptance.SatisfiedCount}/{acceptance.RequiredCount} atendidos", success: acceptance.RequiredSatisfied);
                var completionDelta = workspaceBaseline?.ChangesProducedNow(workspace) ?? engine.Evidence.FilesChanged.Where(x => x != "(patch)").ToArray();
                var significant = workspaceBaseline is null ? new SignificantChangeDecision(true, 0, "baseline indisponível") : SignificantChangeGate.Evaluate(task, workspace, workspaceBaseline, acceptance);
                var completion = CompletionGate.BeforeValidation(requirements, acceptance, completionDelta, new(significant.Passed, recoveryEngine.HasBlockingStagnation, engine.HighPriorityVisualIssueOpen, engine.RenderVersion > 0 && engine.InspectedRenderVersion == engine.RenderVersion));
                if (!completion.Allowed)
                {
                    Emit(ExecutionEventType.PolicyRecovery, "Final prematuro rejeitado antes da validação", success: true, details: completion.Reason);
                    turns.Add(new("assistant", raw)); turns.Add(new("user", $"COMPLETION GATE: {completion.Reason} Critérios pendentes: {acceptance.PendingSummary()}. Continue a implementação; não tente finalizar novamente sem nova evidência."));
                    continue;
                }
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
                    var actual = completionDelta.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    var declared = final.FilesChanged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (actual.Length == 0)
                    {
                        // A non-empty git diff can predate this job. It proves the workspace is dirty,
                        // not that this run changed anything. Do not spend retry budget here: redirect
                        // the model back to inspection/editing and reserve attempts for real tool failures.
                        const string scopeReason = "Esta execução ainda não aplicou nenhuma alteração. O git diff existente pode ser anterior à tarefa.";
                        Emit(ExecutionEventType.PolicyRecovery, "Ainda falta editar nesta execução", success: true, details: scopeReason);
                        turns.Add(new("assistant", raw));
                        turns.Add(new("user", $"EXECUÇÃO INCOMPLETA: {scopeReason} Leia os arquivos alvo de site-teste e aplique uma alteração estruturada antes de tentar finalizar. Não use git_diff como prova de edição desta execução."));
                        continue;
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
                var record = harvester.Harvest(workspace, model.ModelId, task, plan, trace, evidence, finalSummary, jobWatch.Elapsed, origin);
                await dataset.SaveCompletedAsync(workspace, record, ct);
                Emit(ExecutionEventType.JobCompleted, "Tarefa concluída", duration: null, success: true, metadata: new Dictionary<string, string> { ["files_changed"] = evidence.FilesChanged.Count.ToString() });
                return new(finalSummary, engine.Phase, evidence, record.TaskId, new(recoveryEngine.NoEffectiveChangeCount, recoveryEngine.RecoveryCount, completionDelta.Count, false, trace.Count(x => x.Kind == "action_requested")));
            }
            if (!engine.IsTerminal) engine.Fail($"Limite de {maxSteps} etapas atingido.");
            Emit(ExecutionEventType.JobFailed, engine.LastError ?? "A tarefa falhou.", success: false);
            if (dataset is DatasetService concreteDataset) await concreteDataset.SaveRejectedAsync(task, model.ModelId, engine.LastError ?? "falha", ct);
            return new(engine.LastError ?? "A tarefa falhou.", engine.Phase, engine.Evidence, Metrics: new(recoveryEngine.NoEffectiveChangeCount, recoveryEngine.RecoveryCount, workspaceBaseline?.ChangesProducedNow(workspace).Count ?? 0, false, trace.Count(x => x.Kind == "action_requested")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { engine.Cancel(); Emit(ExecutionEventType.JobCancelled, "Tarefa cancelada pelo usuário", success: false); throw; }
    }

    static bool RecordProgress(ProgressTracker tracker, MiauAction action, ToolResult result, AcceptancePlan acceptance)
    {
        if (!result.Success) return false;
        if (result.Metadata.TryGetValue("changed_path", out var changed)) return tracker.Record(ProgressKind.Change, changed + ":" + result.Output);
        if (result.Metadata.TryGetValue("inspected_path", out var inspected)) return tracker.Record(ProgressKind.Inspection, inspected);
        if (action.Action == ToolNames.GitDiff) return tracker.Record(ProgressKind.Diff, result.Output);
        if (result.Metadata.ContainsKey("validation")) return tracker.Record(ProgressKind.Validation, result.Output);
        if (result.Metadata.TryGetValue("screenshot_hash", out var hash))
            return tracker.Record(ProgressKind.Visual, string.Join('|', hash, result.Metadata.GetValueOrDefault("viewport", ""), result.Metadata.GetValueOrDefault("visual_inspection", "render"), action.Arguments.GetValueOrDefault("criteria", "")));
        return false;
    }

    static List<ModelTurn> CompactContext(List<ModelTurn> turns, IReadOnlyList<string> plan, JobEvidence evidence, AcceptancePlan acceptance)
    {
        var recent = turns.TakeLast(6).ToList();
        recent.Insert(0, new("user", $"RETOMADA APÓS TIMEOUT. Preserve o trabalho atual. Plano: {string.Join(" | ", plan)}. Arquivos inspecionados: {string.Join(", ", evidence.FilesInspected)}. Arquivos alterados: {string.Join(", ", evidence.FilesChanged)}. Critérios pendentes: {acceptance.PendingSummary()}. Não reinicie a tarefa."));
        return recent;
    }

    public static bool HasBroadVisualEvidence(JobEvidence evidence)
    {
        // A broad redesign needs either multiple files changed (typical HTML/CSS/JS)
        // or repeated structural work in one file. JobEvidence currently tracks unique
        // files, so require >=2 files to prevent a one-line HTML tweak from satisfying
        // a site-wide redesign request.
        return evidence.FilesChanged.Count >= 2;
    }

    public static bool IsBroadVisualRewrite(string task)
    {
        var t = task.ToLowerInvariant();
        var broad = new[] { "melhore significativamente", "reformul", "redesign", "reestrutur", "reconstru", "layout completo", "site completo", "landing page" };
        var structure = new[] { "header", "hero", "footer", "seção", "secao", "responsiv", "layout", "interface", "site" };
        return broad.Any(t.Contains) || structure.Count(t.Contains) >= 3;
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
