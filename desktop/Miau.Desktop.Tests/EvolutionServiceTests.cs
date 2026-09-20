using System.Text.Json;
using System.Net;
using System.Net.Sockets;
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
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await using var dashboard = new EvolutionDashboardService(new EvolutionService(root), "model", $"http://127.0.0.1:{port}/"); dashboard.Start();
        using var client = new HttpClient(); var json = await client.GetStringAsync(dashboard.Address + "api/evolution");
        Assert.Contains("\"TasksTotal\":0", json); Assert.StartsWith("http://127.0.0.1:", dashboard.Address);
    }
    [Fact] public async Task QualityAverageIgnoresLegacyRecordsWithoutScore()
    {
        var completed = Path.Combine(root, "dataset", "completed"); Directory.CreateDirectory(completed);
        var lines = new[] { "{\"Timestamp\":\"2026-01-01T00:00:00Z\",\"FinalResult\":\"legacy\"}", "{\"Timestamp\":\"2026-01-02T00:00:00Z\",\"QualityScore\":80}" };
        await File.WriteAllLinesAsync(Path.Combine(completed, "data.jsonl"), lines);
        var snapshot = await new EvolutionService(root).GetSnapshotAsync("model", false);
        Assert.Equal(80, snapshot.AverageQualityScore); Assert.Equal(1, snapshot.QualityEvaluated); Assert.Equal(1, snapshot.LegacyWithoutQualityScore);
    }
    [Fact] public async Task RecentActivityDoesNotExposeRawPrivateContent()
    {
        var completed = Path.Combine(root, "dataset", "completed"); Directory.CreateDirectory(completed);
        const string secret = "password=SUPER-PRIVATE-TOKEN";
        await File.WriteAllTextAsync(Path.Combine(completed, "data.jsonl"), "{\"Timestamp\":\"2026-01-01T00:00:00Z\",\"Task\":\"" + secret + "\",\"FinalResult\":\"" + secret + "\",\"FilesChanged\":[\"a.cs\"]}\n");
        var snapshot = await new EvolutionService(root).GetSnapshotAsync("model", false);
        Assert.Single(snapshot.RecentActivity); Assert.DoesNotContain(secret, snapshot.RecentActivity[0]); Assert.Contains("1 arquivo", snapshot.RecentActivity[0]);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
