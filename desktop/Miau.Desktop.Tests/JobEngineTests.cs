using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class JobEngineTests
{
    static ToolResult Ok(string tool, params (string, string)[] metadata) => ToolResult.Ok(tool, "ok", metadata);
    [Fact] public void ReadOnlyNeedsInspection() { var job = new JobEngine(new(false, true)); job.Start(); Assert.False(job.TryComplete(out _)); job.Observe(Ok(ToolNames.ReadFile, ("inspected_path", "a.cs"))); Assert.True(job.TryComplete(out _)); }
    [Fact] public void ChangeCannotFinishWithoutEdit() { var job = ChangedJob(); Assert.False(job.TryComplete(out var reason)); Assert.Contains("edição", reason); }
    [Fact] public void ModelFinalDoesNotCompleteEngine() { var job = ChangedJob(); Assert.Equal(JobPhase.Inspecting, job.Phase); Assert.False(job.TryComplete(out _)); }
    [Fact] public void EditWithoutDiffFails() { var job = ChangedJob(); job.Observe(Ok(ToolNames.ReplaceInFile, ("changed_path", "a.cs"))); Assert.False(job.TryComplete(out _)); }
    [Fact] public void EditDiffAndBuildComplete() { var job = ChangedJob(); CompleteEvidence(job); Assert.True(job.TryComplete(out _)); Assert.Equal(JobPhase.Completed, job.Phase); }
    [Fact] public void EmptyDiffFails() { var job = ChangedJob(); job.Observe(Ok(ToolNames.WriteFile, ("changed_path", "a.cs"))); job.Observe(Ok(ToolNames.GitDiff, ("has_changes", "false"))); Assert.False(job.TryComplete(out _)); }
    [Fact] public void BuildFailureDoesNotCountAsTest() { var job = ChangedJob(); job.Observe(ToolResult.Fail(ToolNames.Build, "compile error")); Assert.False(job.Evidence.ValidationRan); }
    [Fact] public void CorrectionAfterBuildFailureCanComplete() { var job = ChangedJob(); job.Observe(ToolResult.Fail(ToolNames.Build, "compile error")); Assert.True(job.RecordFailure("compile error")); CompleteEvidence(job); Assert.True(job.TryComplete(out _)); }
    [Fact] public void AttemptLimitFails() { var job = new JobEngine(new(true, false), 2); Assert.True(job.RecordFailure("one")); Assert.False(job.RecordFailure("two")); Assert.Equal(JobPhase.Failed, job.Phase); }
    [Fact] public void CancellationIsTerminal() { var job = new JobEngine(new(false, true)); job.Start(); job.Cancel(); Assert.Equal(JobPhase.Cancelled, job.Phase); }
    [Fact] public void ToolErrorIsRecorded() { var job = new JobEngine(new(false, true)); job.Observe(ToolResult.Fail(ToolNames.ReadFile, "denied")); Assert.Equal("denied", job.Evidence.LastError); }
    [Fact] public void InvalidProtocolIsRejected() { Assert.False(BrainResponse.TryParse("vou editar", out _, out var error)); Assert.Contains("JSON inválido", error); }
    static JobEngine ChangedJob() { var job = new JobEngine(new(true, false)); job.Start(); job.BeginInspection(); job.Observe(Ok(ToolNames.ReadFile, ("inspected_path", "a.cs"))); return job; }
    static void CompleteEvidence(JobEngine job) { job.Observe(Ok(ToolNames.ReplaceInFile, ("changed_path", "a.cs"))); job.Observe(Ok(ToolNames.GitDiff, ("has_changes", "true"))); job.Observe(Ok(ToolNames.Build, ("validation", "true"))); }
}
