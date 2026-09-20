using System.Text.Json;

namespace Miau.Desktop;

public sealed record TrainingSchedule(bool Enabled = false, int IntervalHours = 24, int MaxTasksPerCycle = 3, int RepetitionsPerScenario = 2, int MaximumLevel = 8);
public sealed record TrainingCycleResult(DateTimeOffset StartedAt, int Attempted, int Completed, int Failed, int Rejected = 0);
public sealed record TrainingScenarioResult(DateTimeOffset At, int Level, string Scenario, int Repetition, bool Passed, double DurationSeconds,
    int Actions, int EffectiveChanges, int NoEffectiveChanges, int Recoveries, bool HumanIntervention, bool Validation, bool OutOfScopeChanges, string? Failure);

public sealed class TrainingScheduler
{
    readonly AgentService agent;
    readonly string appData;
    public TrainingScheduler(AgentService agent, string? appData = null)
    {
        this.agent = agent;
        this.appData = appData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU");
    }

    public async Task<TrainingCycleResult> RunCycleAsync(string root, TrainingSchedule schedule, CancellationToken ct, Action<string>? progress = null)
    {
        if (!schedule.Enabled) return new(DateTimeOffset.Now, 0, 0, 0);
        if (!ExecutionCoordination.Shared.TryAcquire(root, ExecutionKind.Training, out var coordination)) { progress?.Invoke("Treino aguardando: existe uma execução interativa ativa."); return new(DateTimeOffset.Now, 0, 0, 0); }
        using var coordinationLease = coordination;
        var started = DateTimeOffset.Now; var attempted = 0; var completed = 0; var failed = 0; var rejected = 0;
        var scenarios = Tasks().Where(x => x.Level <= Math.Clamp(schedule.MaximumLevel, 1, 8)).Take(Math.Clamp(schedule.MaxTasksPerCycle, 1, 8));
        foreach (var runCase in scenarios.SelectMany(task => Enumerable.Range(1, Math.Clamp(schedule.RepetitionsPerScenario, 2, 5)).Select(repetition => (task, repetition))))
        {
            var task = runCase.task; var repetition = runCase.repetition; var watch = System.Diagnostics.Stopwatch.StartNew(); AgentRunResult? agentRun = null; var passed = false; string? failure = null;
            ct.ThrowIfCancellationRequested(); attempted++;
            var temp = Path.Combine(Path.GetTempPath(), "miau-training", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                await SeedAsync(temp, task.Id, ct);
                progress?.Invoke($"Treino nível {task.Level} · {task.Id} · repetição {repetition}: {task.Title}");
                var deferred = new DeferredDatasetService();
                agentRun = await agent.RunForTrainingAsync(temp, task.Prompt, ct, x => progress?.Invoke(x), deferred);
                if (agentRun.Phase == JobPhase.Completed && await VerifyAsync(temp, task.Id, ct))
                {
                    await deferred.CommitAsync(new DatasetService(), temp, ct); completed++; passed = true;
                }
                else
                {
                    failure = agentRun.Summary;
                    rejected++;
                    await new DatasetService().SaveRejectedAsync(task.Prompt, agent.Model, $"Treino controlado '{task.Id}' não passou na verificação externa.", ct);
                    progress?.Invoke($"Treino {task.Id} rejeitado: resultado não corresponde ao objetivo controlado.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; failure = DatasetService.Redact(ex.Message); progress?.Invoke($"Treino {task.Id} falhou: {failure}"); }
            finally
            {
                var metrics = agentRun?.Metrics ?? new();
                var observation = new TrainingScenarioResult(DateTimeOffset.UtcNow, task.Level, task.Id, repetition, passed, watch.Elapsed.TotalSeconds,
                    metrics.Actions, metrics.EffectiveChanges, metrics.NoEffectiveChangeCount, metrics.RecoveryCount, metrics.HumanIntervention, agentRun?.Evidence.ValidationPassed == true, false, failure);
                var scenarioDir = Path.Combine(appData, "training"); Directory.CreateDirectory(scenarioDir);
                await File.AppendAllTextAsync(Path.Combine(scenarioDir, "scenarios-v1.jsonl"), JsonSerializer.Serialize(observation) + Environment.NewLine, ct);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
        var result = new TrainingCycleResult(started, attempted, completed, failed, rejected);
        var dir = Path.Combine(appData, "training"); Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir, "cycles.jsonl"), JsonSerializer.Serialize(result) + Environment.NewLine, ct);
        return result;
    }

    static IEnumerable<(int Level,string Id,string Title,string Prompt)> Tasks()
    {
        yield return (1, "edit", "Alteração simples", "Em greeting.html troque exatamente <h1>Hello</h1> por <h1>Olá</h1>. Altere somente esse arquivo.");
        yield return (2, "create", "Criação", "Crie somente about.html com um título Sobre e um parágrafo de apresentação.");
        yield return (3, "multi", "Alteração coerente multi-arquivo", "Atualize index.html para usar class=highlight e style.css para estilizar .highlight com font-weight: bold. Altere somente esses dois arquivos.");
        yield return (4, "recover", "Recovery após NoEffectiveChange", "Garanta target=ready em recovery.txt e produza uma mudança efetiva criando recovery-proof.txt com RECOVERED. Se uma gravação idêntica ocorrer, mude de estratégia. Altere somente esses dois arquivos.");
        yield return (5, "build", "Correção de build controlada", "Corrija syntax.py para ser Python válido e imprimir MIAU build OK. Altere somente syntax.py.");
        yield return (6, "visual", "Tarefa visual simples", "Melhore o componente card em index.html e style.css, mantenha responsividade, renderize desktop e mobile e faça inspeção visual.");
        yield return (7, "landing", "Redesign pequeno", "Reformule a landing page mínima em index.html e style.css com header, hero, CTA e footer responsivos; renderize desktop/mobile e inspecione.");
        yield return (8, "cafeteria", "Cafeteria completa", "Melhore significativamente o site de cafeteria em index.html e style.css com header, hero, produtos, editorial, CTA, footer e responsividade; renderize desktop/mobile e inspecione antes de concluir.");
    }

    static async Task<bool> VerifyAsync(string root, string id, CancellationToken ct)
    {
        static async Task<string> Read(string root, string file, CancellationToken ct) => (await File.ReadAllTextAsync(Path.Combine(root,file),ct)).Trim();
        return id switch
        {
            "edit" => (await Read(root,"greeting.html",ct)).Contains("<h1>Olá</h1>"),
            "create" => File.Exists(Path.Combine(root,"about.html")) && (await Read(root,"about.html",ct)).Contains("Sobre"),
            "multi" => (await Read(root,"index.html",ct)).Contains("highlight") && (await Read(root,"style.css",ct)).Contains(".highlight"),
            "recover" => await Read(root,"recovery.txt",ct) == "target=ready" && File.Exists(Path.Combine(root,"recovery-proof.txt")),
            "build" => (await Read(root,"syntax.py",ct)).Contains("print(") && !string.IsNullOrWhiteSpace(await Read(root,"syntax.py",ct)),
            "visual" or "landing" or "cafeteria" => (await Read(root,"index.html",ct)).Contains("<header", StringComparison.OrdinalIgnoreCase) && (await Read(root,"style.css",ct)).Contains("@media", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    static async Task SeedAsync(string root, string id, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "greeting.html"), "<h1>Hello</h1>\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "recovery.txt"), "target=ready\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "syntax.py"), "print('broken'\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width\"><link rel=\"stylesheet\" href=\"style.css\"></head><body><main><h1>Minimal</h1></main></body></html>\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "style.css"), "body { font-family: sans-serif; }\n", ct);
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var args in new[]{ new[]{"init"}, new[]{"add","."}, new[]{"-c","user.name=MIAU Training","-c","user.email=miau@local","commit","-m","seed"} })
        { psi.ArgumentList.Clear(); foreach(var a in args) psi.ArgumentList.Add(a); using var p=System.Diagnostics.Process.Start(psi)!; await p.WaitForExitAsync(ct); }
    }

    sealed class DeferredDatasetService : IDatasetService
    {
        TrainingRecord? record;
        public Task SaveCompletedAsync(string workspace, TrainingRecord value, CancellationToken ct) { record = value; return Task.CompletedTask; }
        public Task CommitAsync(IDatasetService target, string workspace, CancellationToken ct) => record is null ? Task.CompletedTask : target.SaveCompletedAsync(workspace, record, ct);
    }
}
