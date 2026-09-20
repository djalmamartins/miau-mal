using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class RecoveryReliabilityTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-recovery-tests-" + Guid.NewGuid().ToString("N"));
    public RecoveryReliabilityTests() => Directory.CreateDirectory(root);
    static readonly MiauAction WriteA = new(ToolNames.WriteFile, new() { ["path"] = "index.html", ["content"] = "same" }, "hero");
    static ToolResult NoChange(string path = "index.html") => ToolResult.Ok(ToolNames.WriteFile, "NoEffectiveChange", ("effective_change", "false"), ("target_path", path), ("old_hash", "a"), ("proposed_hash", "a"));
    static AcceptancePlan Plan() => new([new("hero", "hero de alto impacto", AcceptanceType.Modification, true, RequiresTaskDelta: true)]);

    [Fact] public void RepeatedNoEffectiveChangeTriggersRecovery()
    {
        var engine = new RecoveryEngine(); var first = engine.ObserveNoEffectiveChange("redesign", WriteA, NoChange(), Plan());
        Assert.Equal(1, first.Level); Assert.Contains("Nenhum progresso", first.Message); Assert.True(engine.HasUnresolvedStagnation);
    }

    [Fact] public void RecoveryEscalatesAfterEquivalentFailures()
    {
        var engine = new RecoveryEngine(); var plan = Plan(); RecoveryDecision decision = null!;
        foreach (var file in new[] { "index.html", "style.css", "script.js" })
            decision = engine.ObserveNoEffectiveChange("redesign cafeteria", WriteA with { Arguments = new() { ["path"] = file } }, NoChange(file), plan);
        Assert.Equal(3, decision.Level); Assert.Contains("ETAPAS OBRIGATÓRIAS", decision.Context.ToPrompt());
    }

    [Fact] public void PersistentStagnationStopsSafely()
    {
        var engine = new RecoveryEngine(); RecoveryDecision decision = null!;
        for (var i = 0; i < 4; i++) decision = engine.ObserveNoEffectiveChange("redesign", WriteA, NoChange(), Plan());
        Assert.True(decision.Stop); Assert.True(engine.HasBlockingStagnation); Assert.Contains("Arquivos existentes foram preservados", engine.Diagnostic("redesign", Plan()));
    }

    [Fact] public void NoEffectiveChangeDoesNotResetProgress()
    {
        var tracker = new ProgressTracker(); tracker.Record(ProgressKind.Inspection, "index.html"); var version = tracker.Snapshot.Version;
        new RecoveryEngine().ObserveNoEffectiveChange("redesign", WriteA, NoChange(), Plan());
        Assert.Equal(version, tracker.Snapshot.Version);
    }

    [Fact] public void ExistingBaselineHeaderDoesNotSatisfyRedesignCriterion()
    {
        var plan = AcceptancePlanner.Build("melhore significativamente o site com header", new(true, false, true, true));
        Assert.False(plan.Satisfy("header", "header já existia", false)); Assert.Contains(plan.Criteria, x => x.Id == "header" && x.Status == AcceptanceStatus.Pending);
    }

    [Fact] public void TaskProducedHeaderChangeCanSatisfyModificationCriterion()
    {
        var plan = AcceptancePlanner.Build("melhore significativamente o site com header", new(true, false, true, true));
        Assert.True(plan.Satisfy("header", "header alterado no task delta", true));
    }

    [Fact] public void BaselineEvidenceIsDifferentFromTaskEvidence()
    {
        File.WriteAllText(Path.Combine(root, "index.html"), "<header>antigo</header>"); var baseline = WorkspaceBaseline.Capture(root);
        Assert.Empty(baseline.ChangesProducedNow(root)); File.WriteAllText(Path.Combine(root, "index.html"), "<header>novo</header>");
        Assert.Contains("index.html", baseline.ChangesProducedNow(root));
    }

    [Fact] public void SignificantImprovementRequiresTaskDelta()
    {
        File.WriteAllText(Path.Combine(root, "index.html"), "<header></header>"); var baseline = WorkspaceBaseline.Capture(root);
        var decision = SignificantChangeGate.Evaluate("melhore significativamente o site", root, baseline, Plan());
        Assert.False(decision.Passed); Assert.Contains("task delta", decision.Reason);
    }

    [Fact] public void RenderDoesNotEqualVisualInspection()
    {
        var job = new JobEngine(new(true, false, true, true), acceptance: new AcceptancePlan([])); job.Start();
        job.Observe(ToolResult.Ok(ToolNames.RenderPage, "", ("visual_validation", "true"), ("viewport", "1440x1200")));
        Assert.True(job.VisualValidationRan); Assert.False(job.VisualInspectionRan);
    }

    [Fact] public void VisualTaskRequiresInspectionAfterRender()
    {
        var job = new JobEngine(new(true, false, false, true), acceptance: new AcceptancePlan([])); job.Start();
        job.Observe(ToolResult.Ok(ToolNames.ReadFile, "", ("inspected_path", "index.html"))); job.Observe(ToolResult.Ok(ToolNames.WriteFile, "", ("changed_path", "index.html")));
        job.Observe(ToolResult.Ok(ToolNames.GitDiff, "x", ("has_changes", "true"))); job.Observe(ToolResult.Ok(ToolNames.RenderPage, "", ("visual_validation", "true"), ("viewport", "1440x1200")));
        Assert.False(job.TryComplete(out var reason)); Assert.Contains("inspetor visual", reason);
    }

    [Fact] public void RecoveryContextIsCompacted()
    {
        var huge = string.Concat(Enumerable.Repeat("objetivo muito grande ", 500)); var decision = new RecoveryEngine().ObserveNoEffectiveChange(huge, WriteA, NoChange(), Plan());
        Assert.True(decision.Context.ToPrompt().Length < 3000); Assert.DoesNotContain(huge, decision.Context.ToPrompt());
    }

    [Fact] public async Task FailedActionIsNotPositiveTrainingExample()
    {
        var episodes = new RecoveryEpisodeService(Path.Combine(root, "episodes")); var decision = new RecoveryEngine().ObserveNoEffectiveChange("redesign", WriteA, NoChange(), Plan());
        await episodes.RecordAsync(root, "redesign", WriteA, decision, null, "stopped", default);
        Assert.False(Directory.Exists(Path.Combine(root, "dataset", "completed"))); Assert.Contains("\"FinalOutcome\":\"stopped\"", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "episodes")).Single()));
    }

    [Fact] public async Task SuccessfulRecoveryCanBecomeRecoveryExample()
    {
        var directory = Path.Combine(root, "episodes"); var episodes = new RecoveryEpisodeService(directory); var decision = new RecoveryEngine().ObserveNoEffectiveChange("redesign", WriteA, NoChange(), Plan());
        await episodes.RecordAsync(root, "redesign", WriteA, decision, ToolNames.ApplyPatch, "recovered", default);
        var text = await File.ReadAllTextAsync(Directory.GetFiles(directory).Single()); Assert.Contains("\"FinalOutcome\":\"recovered\"", text); Assert.Contains("apply_patch", text);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
