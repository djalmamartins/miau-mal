using System.Text.Json;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class BenchmarkIsolationTests
{
    [Fact]
    public async Task BenchmarkDoesNotModifyRealWorkspace()
    {
        var realWorkspace = Path.Combine(Path.GetTempPath(), "miau-real-workspace-" + Guid.NewGuid().ToString("N"));
        var manifestRoot = Path.Combine(Path.GetTempPath(), "miau-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realWorkspace);
        Directory.CreateDirectory(manifestRoot);
        var protectedFile = Path.Combine(realWorkspace, "do-not-touch.txt");
        await File.WriteAllTextAsync(protectedFile, "original");
        var manifest = Path.Combine(manifestRoot, "cases.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new[]
        {
            new BenchmarkCase("readonly", "Leia README.md e descreva o conteúdo.", new() { ["README.md"] = "fixture" }, [], new(), ReadOnly: true)
        }));

        try
        {
            var results = await new BenchmarkService().RunAsync(manifest, new FinalOnlyModel(), CancellationToken.None);

            Assert.Single(results);
            Assert.Equal("original", await File.ReadAllTextAsync(protectedFile));
            Assert.Equal("do-not-touch.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(realWorkspace))));
        }
        finally
        {
            Directory.Delete(realWorkspace, true);
            Directory.Delete(manifestRoot, true);
        }
    }

    sealed class FinalOnlyModel : IModelAdapter
    {
        public string ModelId => "benchmark-isolation";
        public Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) =>
            Task.FromResult("{\"type\":\"final\",\"summary\":\"README analisado sem alterações\",\"files_changed\":[]}");
    }
}
