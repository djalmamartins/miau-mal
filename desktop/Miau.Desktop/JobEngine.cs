namespace Miau.Desktop;

public enum JobPhase { Received, Understanding, Inspecting, Planning, Executing, Verifying, Testing, Completed, Failed, Cancelled }

public sealed record JobRequirements(bool RequiresChange, bool ReadOnly, bool RequiresValidation = true, bool RequiresVisualValidation = false);
public sealed record JobEvidence(IReadOnlyCollection<string> FilesInspected, IReadOnlyCollection<string> FilesChanged,
    bool HasGitDiff, bool ValidationRan, bool ValidationPassed, int Attempts, string? LastError, bool VisualValidationRan = false);

public sealed class JobEngine
{
    readonly HashSet<string> inspected = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
    public JobEngine(JobRequirements requirements, int maxAttempts = 4) { Requirements = requirements; MaxAttempts = Math.Max(1, maxAttempts); }
    public JobRequirements Requirements { get; }
    public int MaxAttempts { get; }
    public int Attempts { get; private set; }
    public JobPhase Phase { get; private set; } = JobPhase.Received;
    public bool HasGitDiff { get; private set; }
    public bool ValidationRan { get; private set; }
    public bool ValidationPassed { get; private set; }
    public string? LastError { get; private set; }
    public bool VisualValidationRan { get; private set; }
    public bool IsTerminal => Phase is JobPhase.Completed or JobPhase.Failed or JobPhase.Cancelled;
    public event Action<JobPhase, string>? StateChanged;
    public JobEvidence Evidence => new(inspected.ToArray(), changed.ToArray(), HasGitDiff, ValidationRan, ValidationPassed, Attempts, LastError, VisualValidationRan);

    public void Start() => Transition(JobPhase.Understanding, "Entendendo a tarefa");
    public void BeginInspection() => Transition(JobPhase.Inspecting, "Inspecionando o projeto");
    public void BeginPlanning() => Transition(JobPhase.Planning, "Planejando a execução");
    public void Observe(ToolResult result)
    {
        if (!result.Success) { LastError = result.Error ?? "Ferramenta retornou erro."; return; }
        if (result.Metadata.TryGetValue("inspected_path", out var inspectedPath) && !string.IsNullOrWhiteSpace(inspectedPath)) inspected.Add(inspectedPath);
        if (result.Metadata.TryGetValue("changed_path", out var changedPath) && !string.IsNullOrWhiteSpace(changedPath))
        { changed.Add(changedPath); Transition(JobPhase.Executing, "Editando arquivos"); }
        if (result.Metadata.TryGetValue("visual_validation", out var visual) && visual == "true") VisualValidationRan = true;
        if (result.Tool == ToolNames.GitDiff)
        { HasGitDiff = result.Metadata.TryGetValue("has_changes", out var value) && value == "true"; Transition(JobPhase.Verifying, "Verificando alterações"); }
        if (result.Metadata.TryGetValue("validation", out var validation) && validation == "true")
        { ValidationRan = true; ValidationPassed = true; Transition(JobPhase.Testing, "Testando o projeto"); }
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
            if (Requirements.RequiresVisualValidation && !VisualValidationRan) { reason = "A tarefa visual ainda não foi renderizada e inspecionada."; return false; }
        }
        reason = ""; Transition(JobPhase.Completed, "Concluído"); return true;
    }
    public void Fail(string reason) { LastError = reason; Transition(JobPhase.Failed, reason); }
    public void Cancel() => Transition(JobPhase.Cancelled, "Cancelado");
    void Transition(JobPhase phase, string description) { if (IsTerminal) return; Phase = phase; StateChanged?.Invoke(phase, description); }
}
