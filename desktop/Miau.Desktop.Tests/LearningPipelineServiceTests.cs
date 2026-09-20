using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class LearningPipelineServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-learning-pipeline-" + Guid.NewGuid().ToString("N"));
    public LearningPipelineServiceTests() => Directory.CreateDirectory(root);

    [Fact] public async Task OnlyHumanApprovedHighQualityExamplesAreExported()
    {
        var dataset = new DatasetService(Path.Combine(root, "dataset"));
        await dataset.SaveCompletedAsync(root, Record("approved"), default); await dataset.SaveCompletedAsync(root, Record("pending"), default);
        var pipeline = new LearningPipelineService(root); await pipeline.SetVerdictAsync("approved", TrainingVerdict.Approved, null, default);
        var file = await pipeline.ExportApprovedForLoraAsync(Path.Combine(root, "export"), default);
        var text = await File.ReadAllTextAsync(file); Assert.Contains("task-approved", text); Assert.DoesNotContain("task-pending", text);
        var readiness = await pipeline.GetReadinessAsync(default); Assert.Equal(1, readiness.Approved); Assert.Equal(1, readiness.PendingReview);
    }

    [Fact] public async Task TrainingExampleRequiresHumanApproval()
    {
        var dataset = new DatasetService(Path.Combine(root, "dataset")); await dataset.SaveCompletedAsync(root, Record("pending-approval"), default);
        var pipeline = new LearningPipelineService(root); var file = await pipeline.ExportApprovedForLoraAsync(Path.Combine(root, "approval-export"), default);
        Assert.Empty(await File.ReadAllLinesAsync(file)); Assert.Equal(1, (await pipeline.GetReadinessAsync(default)).PendingReview);
    }

    [Fact] public async Task RejectedExamplesDoNotCountTowardLoraReadiness()
    {
        var dataset = new DatasetService(Path.Combine(root, "dataset")); await dataset.SaveCompletedAsync(root, Record("rejected"), default);
        var pipeline = new LearningPipelineService(root); await pipeline.SetVerdictAsync("rejected", TrainingVerdict.Rejected, "incompleto", default);
        var readiness = await pipeline.GetReadinessAsync(default); Assert.Equal(0, readiness.Approved); Assert.Equal(1, readiness.Rejected); Assert.False(readiness.ReadyForLora);
    }

    static TrainingRecord Record(string id) => new(id, DateTimeOffset.UtcNow, "v", "model", "project-" + id, "task-" + id, [], [], [], [], ["a.cs"], [], null, "test", [], 0, "resultado") { Success = true };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
