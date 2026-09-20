using System.Text.Json;

namespace Miau.Desktop;

public sealed record TrainingSchedule(bool Enabled = false, int IntervalHours = 24, int MaxTasksPerCycle = 3);
public sealed record TrainingCycleResult(DateTimeOffset StartedAt, int Attempted, int Completed, int Failed, int Rejected = 0);

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
        foreach (var task in Tasks().Take(Math.Clamp(schedule.MaxTasksPerCycle, 1, 10)))
        {
            ct.ThrowIfCancellationRequested(); attempted++;
            var temp = Path.Combine(Path.GetTempPath(), "miau-training", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                await SeedAsync(temp, task.Id, ct);
                progress?.Invoke($"Treino {task.Id}: {task.Title}");
                var deferred = new DeferredDatasetService();
                var run = await agent.RunForTrainingAsync(temp, task.Prompt, ct, x => progress?.Invoke(x), deferred);
                if (run.Phase == JobPhase.Completed && await VerifyAsync(temp, task.Id, ct))
                {
                    await deferred.CommitAsync(new DatasetService(), temp, ct); completed++;
                }
                else
                {
                    rejected++;
                    await new DatasetService().SaveRejectedAsync(task.Prompt, agent.Model, $"Treino controlado '{task.Id}' não passou na verificação externa.", ct);
                    progress?.Invoke($"Treino {task.Id} rejeitado: resultado não corresponde ao objetivo controlado.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; progress?.Invoke($"Treino {task.Id} falhou: {DatasetService.Redact(ex.Message)}"); }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }
        var result = new TrainingCycleResult(started, attempted, completed, failed, rejected);
        var dir = Path.Combine(appData, "training"); Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir, "cycles.jsonl"), JsonSerializer.Serialize(result) + Environment.NewLine, ct);
        return result;
    }

    static IEnumerable<(string Id,string Title,string Prompt)> Tasks()
    {
        yield return ("edit", "Edição controlada", "Altere somente README.md acrescentando ao final uma linha exatamente: MIAU training edit OK");
        yield return ("create", "Criação controlada", "Crie somente training-result.txt contendo exatamente MIAU training create OK");
        yield return ("recover", "Recuperação controlada", "No arquivo config.txt altere exatamente mode=old para mode=new. Não altere outros arquivos.");
        yield return ("delete", "Remoção segura", "Remova somente obsolete.txt. Não altere nenhum outro arquivo.");
        yield return ("multi", "Alteração multi-arquivo", "Altere app.txt para conter app=v2 e test.txt para conter test=v2. Não altere outros arquivos.");
        yield return ("scope", "Respeito de escopo", "Altere somente allowed.txt para conter allowed=v2. Não altere protected.txt.");
    }

    static async Task<bool> VerifyAsync(string root, string id, CancellationToken ct)
    {
        static async Task<string> Read(string root, string file, CancellationToken ct) => (await File.ReadAllTextAsync(Path.Combine(root,file),ct)).Trim();
        return id switch
        {
            "edit" => (await Read(root,"README.md",ct)).EndsWith("MIAU training edit OK", StringComparison.Ordinal),
            "create" => File.Exists(Path.Combine(root,"training-result.txt")) && await Read(root,"training-result.txt",ct) == "MIAU training create OK",
            "recover" => await Read(root,"config.txt",ct) == "mode=new",
            "delete" => !File.Exists(Path.Combine(root,"obsolete.txt")),
            "multi" => await Read(root,"app.txt",ct) == "app=v2" && await Read(root,"test.txt",ct) == "test=v2",
            "scope" => await Read(root,"allowed.txt",ct) == "allowed=v2" && await Read(root,"protected.txt",ct) == "do-not-touch",
            _ => false
        };
    }

    static async Task SeedAsync(string root, string id, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# MIAU Training\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "config.txt"), "mode=old\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "obsolete.txt"), "remove me\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "app.txt"), "app=v1\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "test.txt"), "test=v1\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "allowed.txt"), "allowed=v1\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "protected.txt"), "do-not-touch\n", ct);
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
