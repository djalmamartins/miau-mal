using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ReliabilityTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-reliability-" + Guid.NewGuid().ToString("N"));
    public ReliabilityTests() => Directory.CreateDirectory(root);

    [Fact] public void DetectsAbcCycleWithoutProgress()
    {
        var tracker = new ProgressTracker(); StagnationResult result = new(false,0,"","");
        foreach (var path in new[] { "a", "b", "c", "a", "b", "c" }) result = tracker.ObserveAction(new(ToolNames.ReadFile, new() { ["path"] = path }, null));
        Assert.True(result.Detected); Assert.Contains("read_file", result.Cycle);
    }
    [Fact] public void ReReadingSameFileIsNotProgress()
    {
        var tracker = new ProgressTracker(); Assert.True(tracker.Record(ProgressKind.Inspection, "a.cs")); Assert.False(tracker.Record(ProgressKind.Inspection, "a.cs"));
        tracker.ObserveAction(new(ToolNames.ReadFile, new() { ["path"] = "a.cs" }, null));
        Assert.True(tracker.ObserveAction(new(ToolNames.ReadFile, new() { ["path"] = "a.cs" }, null)).Detected);
    }
    [Fact] public void IdenticalDiffIsNotProgress()
    { var tracker = new ProgressTracker(); Assert.True(tracker.Record(ProgressKind.Diff, "+x")); Assert.False(tracker.Record(ProgressKind.Diff, "+x")); }
    [Fact] public void RealChangeAndNewCriterionAreProgress()
    { var tracker = new ProgressTracker(); Assert.True(tracker.Record(ProgressKind.Change, "a.cs:hash1")); Assert.True(tracker.Record(ProgressKind.Criterion, "header")); Assert.Equal(2, tracker.Snapshot.Version); }

    [Fact] public async Task FirstModelTimeoutRecoversWithPreservedTask()
    {
        var events = new List<ExecutionEvent>(); var model = new TimeoutModel(1);
        var result = await new AgentOrchestrator(model, new ReadTools(), new NullDataset()).RunAsync(root, "apenas analise", new(false,true), default, eventSink: events.Add);
        Assert.Equal(JobPhase.Completed, result.Phase); Assert.Contains(events, x => x.Type == ExecutionEventType.ModelTimeout); Assert.Equal(2, model.Calls);
    }
    [Fact] public async Task ConsecutiveModelTimeoutsFailClearly()
    {
        var result = await new AgentOrchestrator(new TimeoutModel(2), new ReadTools(), new NullDataset()).RunAsync(root, "apenas analise", new(false,true), default);
        Assert.Equal(JobPhase.Failed, result.Phase); Assert.Contains("duas vezes", result.Summary);
    }
    [Fact] public void InteractiveExecutionBlocksTraining()
    {
        var coordinator = new ExecutionCoordinator(); Assert.True(coordinator.TryAcquire(root, ExecutionKind.Interactive, out var lease));
        using (lease) Assert.False(coordinator.TryAcquire(root, ExecutionKind.Training, out _));
        Assert.True(coordinator.TryAcquire(root, ExecutionKind.Training, out var after)); after!.Dispose();
    }
    [Fact] public void PreExistingGitChangesAreNotTaskEvidence()
    {
        File.WriteAllText(Path.Combine(root,"old.txt"), "dirty"); var baseline = WorkspaceBaseline.Capture(root); File.WriteAllText(Path.Combine(root,"new.txt"), "job");
        var changes = baseline.ChangesProducedNow(root); Assert.Contains("new.txt", changes); Assert.DoesNotContain("old.txt", changes);
    }
    [Fact] public void TaskDeltaPreservesUserChanges()
    {
        var userFile = Path.Combine(root, "user.txt"); File.WriteAllText(userFile, "user work");
        var baseline = WorkspaceBaseline.Capture(root); File.WriteAllText(Path.Combine(root, "agent.txt"), "agent work");
        Assert.Equal("user work", File.ReadAllText(userFile)); Assert.Equal(["agent.txt"], baseline.ChangesProducedNow(root));
    }
    [Fact] public void RequiredAcceptanceCriterionBlocksCompletion()
    {
        var plan = new AcceptancePlan([new("required", "obrigatório", AcceptanceType.Structural, true)]); var job = new JobEngine(new(false,true), acceptance: plan); job.Start(); job.Observe(ToolResult.Ok(ToolNames.ReadFile,"ok",("inspected_path","a")));
        Assert.False(job.TryComplete(out var reason)); Assert.Contains("obrigatório", reason); plan.Satisfy("required","evidência"); Assert.True(job.TryComplete(out _));
    }
    [Fact] public void ResponsiveVisualTaskRequiresDesktopAndMobile()
    {
        var req = new JobRequirements(true,false,true,true); var plan = AcceptancePlanner.Build("site responsivo", req); var job = new JobEngine(req, acceptance: plan); job.Start(); job.Observe(ToolResult.Ok(ToolNames.ReadFile,"",("inspected_path","index.html"))); job.Observe(ToolResult.Ok(ToolNames.WriteFile,"",("changed_path","index.html"))); job.Observe(ToolResult.Ok(ToolNames.GitDiff,"",("has_changes","true"))); job.Observe(ToolResult.Ok(ToolNames.Test,"",("validation","true"))); job.Observe(ToolResult.Ok(ToolNames.RenderPage,"",("visual_validation","true"),("viewport","1440x1200"))); job.Observe(ToolResult.Ok(ToolNames.InspectVisual,"",("visual_inspection","true"),("visual_verdict","approved")));
        Assert.False(job.TryComplete(out var reason)); Assert.Contains("mobile", reason); plan.Satisfy("responsive-structure", "viewport + media"); job.Observe(ToolResult.Ok(ToolNames.RenderPage,"",("visual_validation","true"),("viewport","390x844"))); Assert.True(job.TryComplete(out _));
    }
    [Fact] public void IdenticalScreenshotIsNotProgress()
    {
        var policy = new VisualRevisionPolicy(2); Assert.True(policy.CanRevise("a","c1",out _)); Assert.False(policy.CanRevise("a","c1",out var same)); Assert.Contains("idênticos", same);
        Assert.True(policy.CanRevise("b","c1",out _)); Assert.False(policy.CanRevise("c","c1",out var limit)); Assert.Contains("Limite", limit);
    }
    [Fact] public void VisualReviewRequiresActionableEvidence()
    { Assert.False(ToolExecutor.HasActionableVisualProblem("VEREDITO: REVISAR. Poderia melhorar.")); Assert.True(ToolExecutor.HasActionableVisualProblem("PROBLEMA: contraste. EVIDÊNCIA: botão. PRIORIDADE: alta. VEREDITO: REVISAR")); }

    [Fact] public void BroadVisualTaskRequiresStructuralEvidence()
    {
        var plan = AcceptancePlanner.Build("Melhore significativamente com header, hero, produtos, editorial, CTA, footer e responsividade", new(true, false, true, true));
        var required = plan.Criteria.Where(x => x.Required).Select(x => x.Id).ToArray();
        Assert.Contains("header", required); Assert.Contains("hero", required); Assert.Contains("products", required); Assert.Contains("editorial", required);
        Assert.Contains("cta", required); Assert.Contains("footer", required); Assert.Contains("responsive-structure", required);
        Assert.Contains("desktop-render", required); Assert.Contains("mobile-render", required); Assert.Contains("visual-inspection", required);
    }

    sealed class TimeoutModel(int failures) : IModelAdapter { public string ModelId => "timeout"; public int Calls { get; private set; } public Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) { Calls++; if (Calls <= failures) throw new TimeoutException("90s"); return Task.FromResult("{\"type\":\"final\",\"summary\":\"analisado\",\"files_changed\":[]}"); } }
    sealed class ReadTools : IToolExecutor { public Task<ToolResult> ExecuteAsync(string workspace, MiauAction action, bool readOnly, CancellationToken ct) => Task.FromResult(ToolResult.Ok(action.Action,"files",("inspected_path","."))); public Task<ToolResult> ValidateAsync(string workspace, CancellationToken ct) => Task.FromResult(ToolResult.Ok(ToolNames.Test,"ok",("validation","true"))); }
    sealed class NullDataset : IDatasetService { public Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct) => Task.CompletedTask; }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
