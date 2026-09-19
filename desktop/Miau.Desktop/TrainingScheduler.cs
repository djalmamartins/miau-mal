using System.Text.Json;

namespace Miau.Desktop;

public sealed record TrainingSchedule(bool Enabled = false, int IntervalHours = 6, int MaxTasksPerCycle = 3);
public sealed record TrainingCycleResult(DateTimeOffset StartedAt, int Attempted, int Completed, int Failed);

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
        var started = DateTimeOffset.Now; var attempted = 0; var completed = 0; var failed = 0;
        foreach (var task in Tasks().Take(Math.Clamp(schedule.MaxTasksPerCycle, 1, 10)))
        {
            ct.ThrowIfCancellationRequested(); attempted++;
            var temp = Path.Combine(Path.GetTempPath(), "miau-training", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                await SeedAsync(temp, task.Id, ct);
                progress?.Invoke($"Treino {task.Id}: {task.Title}");
                await agent.RunAsync(temp, task.Prompt, ct, x => progress?.Invoke(x));
                completed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; progress?.Invoke($"Treino {task.Id} falhou: {DatasetService.Redact(ex.Message)}"); }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }
        var result = new TrainingCycleResult(started, attempted, completed, failed);
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
}
