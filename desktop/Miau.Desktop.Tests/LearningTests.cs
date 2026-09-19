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
    [Fact] public async Task DeleteFileRemovesOnlyWorkspaceFile()
    {
        var file = Path.Combine(root, "obsolete.txt"); await File.WriteAllTextAsync(file, "old");
        var action = new MiauAction(ToolNames.DeleteFile, new() { ["path"] = "obsolete.txt" }, null);
        var result = await new ToolExecutor().ExecuteAsync(root, action, false, default);
        Assert.True(result.Success); Assert.False(File.Exists(file)); Assert.Equal("obsolete.txt", result.Metadata["changed_path"]);
        var escape = new MiauAction(ToolNames.DeleteFile, new() { ["path"] = "../outside.txt" }, null);
        Assert.False((await new ToolExecutor().ExecuteAsync(root, escape, false, default)).Success);
    }
    [Fact] public void GitStatusParserReturnsOnlyChangedPaths()
    { Assert.Equal(["src/a.cs", "new.txt"], GitHubJobService.ParseChangedFiles(" M src/a.cs\0?? new.txt\0").ToArray()); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public void SelfRepairRejectsRegression()
    {
        Assert.True(SelfRepairService.Accept(70, 70, true, true));
        Assert.True(SelfRepairService.Accept(70, 80, true, true));
        Assert.False(SelfRepairService.Accept(80, 70, true, true));
        Assert.False(SelfRepairService.Accept(70, 90, false, true));
        Assert.False(SelfRepairService.Accept(70, 90, true, false));
    }

    [Fact] public async Task SelfRepairDetectionRequiresEnoughEvidence()
    {
        var dataset = Path.Combine(root, "dataset", "recovery"); Directory.CreateDirectory(dataset);
        var file = Path.Combine(dataset, "failures.jsonl");
        await File.WriteAllLinesAsync(file, [
            "{\"FailedTool\":\"replace_in_file\"}",
            "{\"FailedTool\":\"replace_in_file\"}"
        ]);
        var repair = new SelfRepairService(root);
        Assert.Empty(await repair.DetectAsync(default, 3));
        var candidates = await repair.DetectAsync(default, 2);
        Assert.Single(candidates);
        Assert.Equal("self-repair-replace-in-file", candidates[0].Id);
    }

    [Fact] public async Task TrainingSchedulerStaysDisabledByDefault()
    {
        var scheduler = new TrainingScheduler(new AgentService(), root);
        var result = await scheduler.RunCycleAsync(root, new TrainingSchedule(), default);
        Assert.Equal(0, result.Attempted);
    }
}
