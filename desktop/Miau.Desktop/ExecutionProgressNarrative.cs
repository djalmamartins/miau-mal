namespace Miau.Desktop;

// Human-readable live status. It only summarizes observed execution events and
// deliberately does not invent conclusions about files, references or validation.
public static class ExecutionProgressNarrative
{
    public static string For(ExecutionEvent ev) => ev.Type switch
    {
        ExecutionEventType.JobStarted => "Entendi o pedido. Vou mapear o projeto antes de alterar qualquer arquivo.",
        ExecutionEventType.PhaseChanged when ev.Phase == JobPhase.Understanding => "Estou identificando os requisitos e as evidências necessárias para concluir.",
        ExecutionEventType.PhaseChanged when ev.Phase == JobPhase.Inspecting => "Estou inspecionando os arquivos relevantes e o estado atual do projeto.",
        ExecutionEventType.PhaseChanged when ev.Phase == JobPhase.Planning => "O contexto foi reunido. Estou organizando a sequência de trabalho.",
        ExecutionEventType.ModelRequestStarted => "Estou definindo o próximo passo com base nas evidências coletadas.",
        ExecutionEventType.ModelRequestCompleted => "Próximo passo definido; vou executar e conferir o resultado.",
        ExecutionEventType.ToolStarted when ev.Description.Contains("Leu", StringComparison.OrdinalIgnoreCase) => "Estou lendo o arquivo necessário antes de editar.",
        ExecutionEventType.ToolCompleted when ev.Description.Contains("Leu", StringComparison.OrdinalIgnoreCase) => "Arquivo inspecionado. Vou usar esse contexto para a próxima alteração.",
        ExecutionEventType.ToolCompleted when ev.Metadata?.GetValueOrDefault("reference_status") == "unavailable" => "A referência externa não ficou disponível; vou continuar apenas com os requisitos e arquivos locais.",
        ExecutionEventType.FileChanged => "Alteração aplicada. Agora vou conferir se ela realmente atende ao pedido.",
        ExecutionEventType.DiffStarted => "Estou conferindo as alterações produzidas nesta execução.",
        ExecutionEventType.BuildStarted or ExecutionEventType.TestsStarted => "As alterações estão prontas para validação. Estou executando as verificações necessárias.",
        ExecutionEventType.BuildCompleted or ExecutionEventType.TestsCompleted => "A validação foi concluída. Vou checar os critérios restantes.",
        ExecutionEventType.PolicyRecovery => "A estratégia anterior não atende aos critérios ainda; estou ajustando o próximo passo sem descartar o trabalho válido.",
        ExecutionEventType.RecoveryStarted => "Detectei repetição sem avanço. Estou mudando a estratégia de forma controlada.",
        ExecutionEventType.RetryStarted => "Uma execução falhou e vou tentar uma correção baseada na evidência retornada.",
        ExecutionEventType.JobCompleted => "Todas as evidências obrigatórias foram coletadas. A tarefa foi concluída.",
        ExecutionEventType.JobFailed => "A execução foi interrompida após esgotar as recuperações disponíveis.",
        ExecutionEventType.JobCancelled => "A tarefa foi cancelada; o estado atual do workspace foi preservado.",
        _ => ev.Description
    };
}
