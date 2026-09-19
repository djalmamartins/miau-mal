using System.Text.Json;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class DatasetServiceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-dataset-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task SecretsAreRedacted()
    {
        var service = new DatasetService(root);
        var record = new TrainingRecord("1", DateTimeOffset.Now, "0", "model", "fingerprint", "token=abc api_key:xyz",
            [], [], [new(DateTimeOffset.Now, "tool", "read", true, "Authorization: Bearer 123\npassword=hunter2")], [], [],
            ["-----BEGIN PRIVATE KEY-----\nprivate\n-----END PRIVATE KEY-----"], "Server=db;Password=pw;", null, [], 0, "done");
        await service.SaveCompletedAsync("/workspace", record, default);
        var text = await File.ReadAllTextAsync(Path.Combine(root, "completed", "miau1-coder-v0.jsonl"));
        Assert.DoesNotContain("hunter2", text); Assert.DoesNotContain("Bearer 123", text); Assert.DoesNotContain("private", text); Assert.DoesNotContain("Password=pw", text); Assert.Contains("REDACTED", text);
    }
    [Fact] public void FingerprintIsStable() => Assert.Equal(DatasetService.Fingerprint(root), DatasetService.Fingerprint(root));
    [Fact] public void QualityRewardsValidationAndPenalizesRetries()
    {
        var good = Record() with { BuildResult = "ok", TestsResult = "13 passed" };
        var weak = Record() with { Retries = 4, Errors = ["x", "y"] };
        Assert.True(DatasetQuality.Evaluate(good).Score > DatasetQuality.Evaluate(weak).Score);
    }
    static TrainingRecord Record() => new("1", DateTimeOffset.Now, "0", "model", "fp", "task", [], [], [], ["a"], ["a"], [], null, null, [], 0, "done");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
