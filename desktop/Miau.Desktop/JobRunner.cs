namespace Miau.Desktop;

public sealed class JobRunner
{
    readonly TimeSpan idlePoll = TimeSpan.FromSeconds(30);

    public event Action<string>? StatusChanged;
    public bool IsRunning { get; private set; }\n    public bool StopAfterCurrentTask { get; set; }

    public async Task RunAsync(
        Func<CancellationToken, Task<AgentJob?>> acquireNext,
        Func<AgentJob, CancellationToken, Task> execute,
        TimeSpan afterTaskDelay,
        CancellationToken ct)
    {
        IsRunning = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                StatusChanged?.Invoke("Procurando próxima tarefa…");
                var job = await acquireNext(ct);
                if (job is null)
                {
                    StatusChanged?.Invoke("Aguardando trabalho");
                    await Task.Delay(idlePoll, ct);
                    continue;
                }

                StatusChanged?.Invoke($"Executando: {job.Title}");
                try
                {
                    await execute(job, ct);
                    StatusChanged?.Invoke($"Concluída: {job.Title}. Próxima tarefa em {afterTaskDelay.TotalMinutes:0.#} min.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Falhou: {job.Title} — {ex.Message}");
                }

                if (StopAfterCurrentTask)\n                {\n                    StatusChanged?.Invoke("Parado após a tarefa atual");\n                    break;\n                }\n\n                await Task.Delay(afterTaskDelay, ct);
            }
        }
        finally
        {
            IsRunning = false;
            StatusChanged?.Invoke("Modo autônomo parado");
        }
    }
}

public sealed record AgentJob(string Id, string Title, string Prompt, string? Branch = null);
