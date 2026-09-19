using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class LearningTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-learning-" + Guid.NewGuid().ToString("N"));
    public LearningTests() => Directory.CreateDirectory(root);
    [Fact] public async Task MemoryIsSeparatedByProjectAndRelevant()
    {
        var memory = new MemoryService(Path.Combine(root, "memory")); var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        await memory.RememberAsync(a, "build Avalonia", "use dotnet build desktop.csproj", default);
        Assert.Contains("dotnet build", await memory.RecallAsync(a, "como fazer build Avalonia", default));
        Assert.Equal("", await memory.RecallAsync(b, "build Avalonia", default));
    }
    [Fact] public async Task SimilarMemoriesAreConsolidated()
    {
        var memory = new MemoryService(Path.Combine(root, "memory"));
        await memory.RememberAsync(root, "convenção do projeto", "arquivos usam nomes PascalCase e namespaces claros", default);
        await memory.RememberAsync(root, "convenção do projeto", "arquivos usam nomes PascalCase e namespaces claros", default);
        var files = Directory.GetFiles(memory.ProjectDirectory(root), "*.jsonl"); Assert.Single(await File.ReadAllLinesAsync(files.Single()));
    }
    [Fact] public void ReplaceLoopChangesStrategy()
    {
        var policy = new EditPolicy(); var action = new MiauAction(ToolNames.ReplaceInFile, new() { ["path"] = "a.cs" }, null);
        policy.Observe(action, ToolResult.Fail(ToolNames.ReplaceInFile, "missing")); policy.Observe(action, ToolResult.Fail(ToolNames.ReplaceInFile, "missing"));
        Assert.False(policy.Allow(action)); Assert.Equal(EditMethod.Write, policy.Choose("a.cs", true, true, 1, 1, false).Method);
    }
    [Fact] public void EditStrategySelectsPatchForMultipleFiles() => Assert.Equal(EditMethod.Patch, new EditPolicy().Choose("a.cs", true, true, 2, 2, false).Method);
    [Fact] public void GitStatusParserReturnsOnlyChangedPaths()
    { Assert.Equal(["src/a.cs", "new.txt"], GitHubJobService.ParseChangedFiles(" M src/a.cs\0?? new.txt\0").ToArray()); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
