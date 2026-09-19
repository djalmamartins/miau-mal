using System.Text.Json;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class EvolutionServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-evolution-" + Guid.NewGuid().ToString("N"));
    [Fact] public async Task ZeroStateContainsOnlyZeros()
    {
        var value = await new EvolutionService(root).GetSnapshotAsync("model", false);
        Assert.Equal(0, value.TasksTotal); Assert.Equal(0, value.Experiences); Assert.Equal(0, value.Memories); Assert.Equal(0, value.SuccessRate);
        Assert.All(value.Skills, x => Assert.Equal(0, x.Attempts));
    }
    [Fact] public async Task AggregatesEvidenceAndPersistsOnlyChangedHistory()
    {
        var completed = Path.Combine(root, "dataset", "completed"); Directory.CreateDirectory(completed);
        var record = new TrainingRecord("1", DateTimeOffset.Now, "v0", "model", "fp", "task", [], [], [new(DateTimeOffset.Now, "tool", "build", true, "ok")], [], ["a"], [], "ok", null, [], 1, "done") { QualityScore = 80, Duration = TimeSpan.FromSeconds(4) };
        await File.WriteAllTextAsync(Path.Combine(completed, "data.jsonl"), JsonSerializer.Serialize(record) + "\n");
        var service = new EvolutionService(root); var first = await service.GetSnapshotAsync("model"); var second = await service.GetSnapshotAsync("model");
        Assert.Equal(1, first.TasksCompleted); Assert.Equal(1, first.BuildsPassed); Assert.Equal(80, first.AverageQualityScore); Assert.Single(second.History);
    }
    [Fact] public async Task LocalApiReturnsZeroState()
    {
        await using var dashboard = new EvolutionDashboardService(new EvolutionService(root), "model"); dashboard.Start();
        using var client = new HttpClient(); var json = await client.GetStringAsync(EvolutionDashboardService.Url + "api/evolution");
        Assert.Contains("\"TasksTotal\":0", json); Assert.StartsWith("http://127.0.0.1:", EvolutionDashboardService.Url);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
