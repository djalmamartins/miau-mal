namespace Miau.Desktop;

public enum JobPhase { Received, Understanding, Inspecting, Planning, Executing, Verifying, Testing, Completed, Failed, Cancelled }

public sealed record JobRequirements(bool RequiresChange, bool ReadOnly, bool RequiresValidation = true, bool RequiresVisualValidation = false);
public sealed record VisualEvidence(string Viewport, string ScreenshotHash, string Provider, string Model, string Verdict, int IssueCount, string Categories = "");
public sealed record JobEvidence(IReadOnlyCollection<string> FilesInspected, IReadOnlyCollection<string> FilesChanged,
    bool HasGitDiff, bool ValidationRan, bool ValidationPassed, int Attempts, string? LastError, bool VisualValidationRan = false, bool VisualInspectionRan = false, bool VisualInspectionPassed = false,
    IReadOnlyList<AcceptanceCriterion>? Criteria = null, int RenderVersion = 0, int InspectedRenderVersion = 0, bool HighPriorityVisualIssueOpen = false, IReadOnlyList<VisualEvidence>? VisualEvidence = null);

public sealed class JobEngine
{
    readonly HashSet<string> inspected = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
    public JobEngine(JobRequirements requirements, int maxAttempts = 4, AcceptancePlan? acceptance = null) { Requirements = requirements; MaxAttempts = Math.Max(1, maxAttempts); Acceptance = acceptance ?? new AcceptancePlan([]); }
    public AcceptancePlan Acceptance { get; }
    public JobRequirements Requirements { get; }
    public int MaxAttempts { get; }
    public int Attempts { get; private set; }
    public JobPhase Phase { get; private set; } = JobPhase.Received;
    public bool HasGitDiff { get; private set; }
    public bool ValidationRan { get; private set; }
    public bool ValidationPassed { get; private set; }
    public string? LastError { get; private set; }
    public bool VisualValidationRan { get; private set; }
    public bool VisualInspectionRan { get; private set; }
    public bool VisualInspectionPassed { get; private set; }
    public int RenderVersion { get; private set; }
    public int InspectedRenderVersion { get; private set; }
    public bool HighPriorityVisualIssueOpen { get; private set; }
    readonly Dictionary<string, string> renderedHashes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, VisualEvidence> visualEvidence = new(StringComparer.OrdinalIgnoreCase);
    public bool IsTerminal => Phase is JobPhase.Completed or JobPhase.Failed or JobPhase.Cancelled;
    public event Action<JobPhase, string>? StateChanged;
    public JobEvidence Evidence => new(inspected.ToArray(), changed.ToArray(), HasGitDiff, ValidationRan, ValidationPassed, Attempts, LastError, VisualValidationRan, VisualInspectionRan, VisualInspectionPassed, Acceptance.Criteria, RenderVersion, InspectedRenderVersion, HighPriorityVisualIssueOpen, visualEvidence.Values.ToArray());

    public void Start() => Transition(JobPhase.Understanding, "Entendendo a tarefa");
    public void BeginInspection() => Transition(JobPhase.Inspecting, "Inspecionando o projeto");
    public void BeginPlanning() => Transition(JobPhase.Planning, "Planejando a execução");
    public void Observe(ToolResult result)
    {
        if (!result.Success) { LastError = result.Error ?? "Ferramenta retornou erro."; return; }
        if (result.Metadata.TryGetValue("inspected_path", out var inspectedPath) && !string.IsNullOrWhiteSpace(inspectedPath)) inspected.Add(inspectedPath);
        if (result.Metadata.TryGetValue("changed_path", out var changedPath) && !string.IsNullOrWhiteSpace(changedPath))
        { changed.Add(changedPath); Acceptance.Satisfy("change", changedPath); Transition(JobPhase.Executing, "Editando arquivos"); }
        if (result.Metadata.TryGetValue("visual_validation", out var visual) && visual == "true")
        {
            VisualValidationRan = true; RenderVersion++;
            var viewport = result.Metadata.GetValueOrDefault("viewport", "1440x1200"); var hash = result.Metadata.GetValueOrDefault("screenshot_hash", "");
            if (!string.IsNullOrWhiteSpace(hash)) renderedHashes[viewport] = hash;
            Acceptance.Satisfy(viewport == "390x844" ? "mobile-render" : "desktop-render", result.Metadata.GetValueOrDefault("screenshot_path", "render"));
        }
        if (IsRealVisualEvidence(result, out var evidence))
        {
            VisualInspectionRan = true; InspectedRenderVersion = RenderVersion; visualEvidence[evidence.Viewport] = evidence;
            VisualInspectionPassed = evidence.Verdict == "approved"; HighPriorityVisualIssueOpen = result.Metadata.GetValueOrDefault("visual_high_priority_open") == "true";
            if (VisualInspectionPassed && !HighPriorityVisualIssueOpen) Acceptance.Satisfy(evidence.Viewport == "390x844" ? "mobile-visual-inspection" : "desktop-visual-inspection", result.Output);
        }
        if (result.Tool == ToolNames.GitDiff)
        { HasGitDiff = result.Metadata.TryGetValue("has_changes", out var value) && value == "true"; Transition(JobPhase.Verifying, "Verificando alterações"); }
        if (result.Metadata.TryGetValue("validation", out var validation) && validation == "true")
        { ValidationRan = true; ValidationPassed = true; Acceptance.Satisfy("validation", result.Output); Transition(JobPhase.Testing, "Testando o projeto"); }
        LastError = null;
    }
    public bool RecordFailure(string error)
    {
        LastError = error; Attempts++;
        if (Attempts < MaxAttempts) return true;
        Fail($"Limite de {MaxAttempts} tentativas atingido: {error}"); return false;
    }
    public bool TryComplete(out string reason)
    {
        if (Requirements.ReadOnly)
        { if (inspected.Count == 0) { reason = "A tarefa somente leitura terminou sem inspeção comprovada."; return false; } }
        else if (Requirements.RequiresChange)
        {
            if (inspected.Count == 0) { reason = "Nenhum arquivo relevante foi inspecionado."; return false; }
            if (changed.Count == 0) { reason = "A tarefa exige alteração, mas nenhuma edição foi aplicada."; return false; }
            if (!HasGitDiff) { reason = "A edição não foi comprovada por um git diff não vazio."; return false; }
            if (Requirements.RequiresValidation && (!ValidationRan || !ValidationPassed)) { reason = "A alteração ainda não passou por build ou teste apropriado."; return false; }
            if (Requirements.RequiresVisualValidation && !VisualValidationRan) { reason = "A tarefa visual ainda não foi renderizada."; return false; }
            if (Requirements.RequiresVisualValidation && !VisualInspectionRan) { reason = "O screenshot ainda não foi analisado pelo inspetor visual."; return false; }
            if (Requirements.RequiresVisualValidation && InspectedRenderVersion != RenderVersion) { reason = "A renderização mais recente ainda não passou por inspeção visual."; return false; }
            if (Requirements.RequiresVisualValidation && !VisualInspectionPassed) { reason = "O inspetor visual pediu revisão da interface; corrija os problemas e faça nova renderização e inspeção."; return false; }
            if (Requirements.RequiresVisualValidation && HighPriorityVisualIssueOpen) { reason = "Existe problema visual de alta prioridade ainda aberto."; return false; }
        }
        if (!Acceptance.RequiredSatisfied) { reason = "Critérios obrigatórios pendentes: " + Acceptance.PendingSummary(); return false; }
        reason = ""; Transition(JobPhase.Completed, "Concluído"); return true;
    }
    public void Fail(string reason) { LastError = reason; Transition(JobPhase.Failed, reason); }
    public void Cancel() => Transition(JobPhase.Cancelled, "Cancelado");
    bool IsRealVisualEvidence(ToolResult result, out VisualEvidence evidence)
    {
        evidence = null!;
        if (result.Metadata.GetValueOrDefault("visual_inspection") != "true" || result.Metadata.GetValueOrDefault("image_included") != "true") return false;
        var viewport = result.Metadata.GetValueOrDefault("viewport", ""); var hash = result.Metadata.GetValueOrDefault("screenshot_hash", "");
        var provider = result.Metadata.GetValueOrDefault("vision_provider", ""); var model = result.Metadata.GetValueOrDefault("vision_model", "");
        if (hash.Length != 64 || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || !renderedHashes.TryGetValue(viewport, out var rendered) || !rendered.Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
        int.TryParse(result.Metadata.GetValueOrDefault("visual_issue_count", "0"), out var count);
        evidence = new(viewport, hash, provider, model, result.Metadata.GetValueOrDefault("visual_verdict", "unavailable"), count, result.Metadata.GetValueOrDefault("visual_issue_categories", "")); return true;
    }
    void Transition(JobPhase phase, string description) { if (IsTerminal) return; Phase = phase; StateChanged?.Invoke(phase, description); }
}
