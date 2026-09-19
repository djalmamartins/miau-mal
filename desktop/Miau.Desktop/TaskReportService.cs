using System.Text;

namespace Miau.Desktop;

public sealed class TaskReportService
{
    public async Task<string> CreateAsync(
        string root,
        AgentJob job,
        string agentId,
        DateTimeOffset startedAt,
        string outcome,
        string summary,
        CancellationToken ct)
    {
        var project = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "reports", project);
        Directory.CreateDirectory(dir);
        var finished = DateTimeOffset.Now;
        var safeId = string.Concat(job.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var file = Path.Combine(dir, $"{finished:yyyyMMdd-HHmmss}-task-{safeId}.md");

        var text = new StringBuilder()
            .AppendLine($"# Relatório MIAU — {job.Title}")
            .AppendLine()
            .AppendLine($"- Agente: {agentId}")
            .AppendLine($"- Início: {startedAt:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"- Fim: {finished:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"- Duração: {(finished - startedAt):hh\\:mm\\:ss}")
            .AppendLine($"- Resultado: {outcome}")
            .AppendLine($"- Branch: {job.Branch ?? "(não informada)"}")
            .AppendLine()
            .AppendLine("## Resumo")
            .AppendLine(summary.Trim())
            .AppendLine()
            .AppendLine("## Próximo passo")
            .AppendLine(outcome.Equals("Concluído", StringComparison.OrdinalIgnoreCase)
                ? "Tarefa finalizada e liberada para revisão/integração."
                : "Revisar a falha registrada antes de considerar a tarefa concluída.")
            .ToString();

        await File.WriteAllTextAsync(file, text, ct);
        return file;
    }
}
