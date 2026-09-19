namespace Miau.Desktop;

public enum JobPhase
{
    Understand,
    Inspect,
    Plan,
    Execute,
    Verify,
    Test,
    Complete,
    Failed
}

public sealed class JobEngine
{
    public JobPhase Phase { get; private set; } = JobPhase.Understand;
    public bool RequiresChange { get; }
    public bool ReadOnly { get; }
    public bool Inspected { get; private set; }
    public bool Changed { get; private set; }
    public bool Verified { get; private set; }
    public bool Tested { get; private set; }
    public bool ToolFailed { get; private set; }

    public JobEngine(bool requiresChange, bool readOnly)
    {
        RequiresChange = requiresChange && !readOnly;
        ReadOnly = readOnly;
    }

    public void Begin(Action<string> progress)
    {
        Set(JobPhase.Understand, progress, "Entendendo a tarefa");
        Set(JobPhase.Inspect, progress, "Inspecionando o projeto");
    }

    public void ObserveTool(string name, bool success, Action<string> progress)
    {
        if (!success)
        {
            ToolFailed = true;
            progress("⚠ Ferramenta retornou erro; o modelo receberá o erro para corrigir.");
            return;
        }

        if (name is "list_files" or "read_file" or "search" or "git_status")
        {
            Inspected = true;
            if (Phase <= JobPhase.Inspect)
                Set(JobPhase.Plan, progress, "Inspeção comprovada; preparando a execução");
        }

        if (name is "write_file" or "replace_in_file")
        {
            Changed = true;
            Set(JobPhase.Execute, progress, "Alteração aplicada no workspace");
        }

        if (name == "git_diff")
        {
            Verified = true;
            Set(JobPhase.Verify, progress, "Alteração verificada pelo Git");
        }

        if (name == "run_command")
        {
            Tested = true;
            Set(JobPhase.Test, progress, "Comando de validação executado");
        }
    }

    public bool CanFinish(out string reason)
    {
        if (ReadOnly)
        {
            reason = "";
            return true;
        }

        if (!RequiresChange)
        {
            reason = "";
            return true;
        }

        if (!Changed)
        {
            reason = "A tarefa exige alteração, mas nenhuma ferramenta de edição foi executada com sucesso.";
            return false;
        }

        if (!Verified)
        {
            reason = "A alteração existe, mas ainda não foi verificada com git_diff.";
            return false;
        }

        reason = "";
        return true;
    }

    public string NextInstruction()
    {
        if (!Inspected)
            return "Inspecione o projeto agora usando list_files, search ou read_file. Não descreva a ação: execute a ferramenta.";
        if (RequiresChange && !Changed)
            return "A inspeção terminou. Aplique agora a alteração solicitada usando replace_in_file ou write_file. Não responda antes de editar de verdade.";
        if (RequiresChange && !Verified)
            return "A alteração foi aplicada. Execute git_diff agora para verificar exatamente o que mudou antes de concluir.";
        return "Finalize com um resumo objetivo do que foi realmente executado e informe validações/testes que de fato ocorreram.";
    }

    public void Complete(Action<string> progress) => Set(JobPhase.Complete, progress, "Tarefa validada pelo motor");
    public void Fail(Action<string> progress, string reason) => Set(JobPhase.Failed, progress, reason);

    void Set(JobPhase phase, Action<string> progress, string description)
    {
        Phase = phase;
        progress($"◆ {phase}: {description}");
    }
}
