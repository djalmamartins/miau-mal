namespace Miau.Desktop;

public enum AcceptanceStatus { Pending, Satisfied, Failed, NotApplicable }
public enum AcceptanceType { Structural, Change, Validation, DesktopRender, MobileRender, VisualInspection }
public sealed record AcceptanceCriterion(string Id, string Description, AcceptanceType Type, bool Required, AcceptanceStatus Status = AcceptanceStatus.Pending, string? Evidence = null, string Source = "task");

public sealed class AcceptancePlan
{
    readonly Dictionary<string, AcceptanceCriterion> criteria;
    public AcceptancePlan(IEnumerable<AcceptanceCriterion> criteria) => this.criteria = criteria.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<AcceptanceCriterion> Criteria => criteria.Values.ToArray();
    public int RequiredCount => criteria.Values.Count(x => x.Required);
    public int SatisfiedCount => criteria.Values.Count(x => x.Required && x.Status == AcceptanceStatus.Satisfied);
    public bool RequiredSatisfied => criteria.Values.Where(x => x.Required).All(x => x.Status is AcceptanceStatus.Satisfied or AcceptanceStatus.NotApplicable);
    public bool Satisfy(string id, string evidence) { if (!criteria.TryGetValue(id, out var item) || item.Status == AcceptanceStatus.Satisfied) return false; criteria[id] = item with { Status = AcceptanceStatus.Satisfied, Evidence = evidence }; return true; }
    public string PendingSummary() => string.Join(", ", criteria.Values.Where(x => x.Required && x.Status == AcceptanceStatus.Pending).Select(x => x.Description));
}

public static class AcceptancePlanner
{
    public static AcceptancePlan Build(string task, JobRequirements requirements)
    {
        var list = new List<AcceptanceCriterion>();
        if (requirements.RequiresChange) { list.Add(new("change", "alteração produzida pela execução", AcceptanceType.Change, true)); list.Add(new("validation", "validação técnica", AcceptanceType.Validation, requirements.RequiresValidation)); }
        if (requirements.RequiresVisualValidation)
        {
            foreach (var (id, label, words) in new[] { ("header","header",new[]{"header","cabeçalho"}), ("hero","hero",new[]{"hero"}), ("products","produtos",new[]{"produto"}), ("editorial","editorial",new[]{"editorial"}), ("cta","CTA",new[]{"cta","chamada"}), ("footer","footer",new[]{"footer","rodapé"}) })
                if (words.Any(x => task.Contains(x, StringComparison.OrdinalIgnoreCase))) list.Add(new(id, label, AcceptanceType.Structural, true));
            list.Add(new("desktop-render", "render desktop 1440x1200", AcceptanceType.DesktopRender, true));
            var responsive = task.Contains("responsiv", StringComparison.OrdinalIgnoreCase) || task.Contains("mobile", StringComparison.OrdinalIgnoreCase);
            if (responsive) list.Add(new("responsive-structure", "viewport meta e CSS responsivo", AcceptanceType.Structural, true));
            list.Add(new("mobile-render", "render mobile 390x844", AcceptanceType.MobileRender, responsive));
            list.Add(new("visual-inspection", "inspeção visual acionável", AcceptanceType.VisualInspection, true));
        }
        return new(list);
    }
}
