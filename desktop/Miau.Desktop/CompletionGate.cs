namespace Miau.Desktop;

public sealed record CompletionDecision(bool Allowed, string Reason);

public static class CompletionGate
{
    public static CompletionDecision BeforeValidation(JobRequirements requirements, AcceptancePlan acceptance, IReadOnlyCollection<string> taskDelta)
    {
        if (requirements.RequiresVisualValidation && acceptance.RequiredCount == 0)
            return new(false, "Nenhum critério de aceitação foi definido para a tarefa visual.");
        if (requirements.RequiresChange && taskDelta.Count == 0)
            return new(false, "Nenhuma alteração efetiva foi produzida nesta execução.");
        var pendingStructural = acceptance.Criteria.Where(x => x.Required && x.Type == AcceptanceType.Structural && x.Status == AcceptanceStatus.Pending).Select(x => x.Description).ToArray();
        if (pendingStructural.Length > 0)
            return new(false, "Critérios estruturais pendentes: " + string.Join(", ", pendingStructural));
        return new(true, "Pré-condições de conclusão atendidas.");
    }
}
