namespace Miau.Desktop;

public sealed record CompletionDecision(bool Allowed, string Reason);
public sealed record CompletionEvidence(bool SignificantChangePassed = true, bool UnresolvedStagnation = false, bool HighPriorityVisualIssueOpen = false, bool LatestRenderInspected = true);

public static class CompletionGate
{
    public static CompletionDecision BeforeValidation(JobRequirements requirements, AcceptancePlan acceptance, IReadOnlyCollection<string> taskDelta, CompletionEvidence? evidence = null)
    {
        evidence ??= new();
        if (requirements.RequiresVisualValidation && acceptance.RequiredCount == 0)
            return new(false, "Nenhum critério de aceitação foi definido para a tarefa visual.");
        if (requirements.RequiresChange && taskDelta.Count == 0)
            return new(false, "Nenhuma alteração efetiva foi produzida nesta execução.");
        if (!evidence.SignificantChangePassed) return new(false, "A mudança produzida ainda não satisfaz o gate de melhoria significativa.");
        if (evidence.UnresolvedStagnation) return new(false, "Existe uma estagnação sem recuperação comprovada.");
        if (evidence.HighPriorityVisualIssueOpen) return new(false, "Existe problema visual de alta prioridade ainda aberto.");
        if (requirements.RequiresVisualValidation && !evidence.LatestRenderInspected) return new(false, "A renderização mais recente ainda não foi inspecionada.");
        var pending = acceptance.Criteria.Where(x => x.Required && x.Status == AcceptanceStatus.Pending && x.Type is not AcceptanceType.Validation and not AcceptanceType.Change).Select(x => x.Description).ToArray();
        if (pending.Length > 0)
            return new(false, "Critérios obrigatórios pendentes: " + string.Join(", ", pending));
        return new(true, "Pré-condições de conclusão atendidas.");
    }
}
