using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class WorkspaceBoundaryTests : IDisposable
{
    readonly string projects = Path.Combine(Path.GetTempPath(), "miau-boundary-" + Guid.NewGuid().ToString("N"), "Projects");
    readonly string initial;
    readonly string target;
    public WorkspaceBoundaryTests() { initial = Path.Combine(projects, "miau", "meu_projeto"); target = Path.Combine(projects, "miau", "teste-site-novo"); Directory.CreateDirectory(initial); }
    WorkspaceBoundary Requested() => new(initial, $"Crie um novo projeto em `{target}`", [projects]);

    [Fact] public void AuthorizedNewDirectoryIsCreatedAndInitialized()
    { var transition = Requested().Initialize(target); Assert.True(transition.Authorized, transition.Code + ": " + transition.Message); Assert.Equal("NewProjectWorkspaceAuthorized", transition.Code); Assert.True(Directory.Exists(target)); }

    [Fact] public async Task ListFilesAndWriteUseOnlyNewRoot()
    {
        await File.WriteAllTextAsync(Path.Combine(initial, "old-secret.txt"), "old"); var boundary = Requested(); Assert.True(boundary.Initialize(target).Authorized);
        var tools = new ToolExecutor(); Assert.True((await tools.ExecuteAsync(boundary.CurrentRoot, new(ToolNames.WriteFile, new() { ["path"]="index.html", ["content"]="<html></html>" }, null), false, default)).Success);
        var list = await tools.ExecuteAsync(boundary.CurrentRoot, new(ToolNames.ListFiles, new() { ["path"]="." }, null), true, default);
        Assert.Contains("index.html", list.Output); Assert.DoesNotContain("old-secret", list.Output); Assert.DoesNotContain("Miau.slnx", list.Output);
    }

    [Fact] public async Task OldWorkspaceIsBlockedAfterTransition()
    { var boundary = Requested(); boundary.Initialize(target); var result = await new ToolExecutor().ExecuteAsync(boundary.CurrentRoot, new(ToolNames.ListFiles, new() { ["path"] = initial }, null), true, default); Assert.False(result.Success); Assert.Contains("fora do workspace", result.Error); }

    [Fact] public void TraversalIsBlocked()
    { var boundary = new WorkspaceBoundary(initial, $"Crie novo projeto em {target}/../escape", [projects]); var result = boundary.Initialize(target + "/../escape"); Assert.False(result.Authorized); Assert.Equal("PathTraversal", result.Code); }

    [Fact] public void OutsideAllowedRootsIsBlocked()
    { var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N")); var boundary = new WorkspaceBoundary(initial, $"Crie novo projeto em {outside}", [projects]); Assert.Equal("OutsideAllowedRoots", boundary.Initialize(outside).Code); }

    [Fact] public void WorkspaceChangeRequiresExplicitRequest()
    { var boundary = new WorkspaceBoundary(initial, $"Leia os arquivos. Caminho informativo: {target}", [projects]); Assert.Equal("ExplicitRequestRequired", boundary.Initialize(target).Code); }

    [Fact] public void SymlinkEscapeIsBlocked()
    {
        if (OperatingSystem.IsWindows()) return; var outside = Path.Combine(Path.GetTempPath(), "miau-outside-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside); var link = Path.Combine(projects, "link"); Directory.CreateDirectory(projects); Directory.CreateSymbolicLink(link, outside);
        try { var destination = Path.Combine(link, "new-project"); var boundary = new WorkspaceBoundary(initial, $"Crie novo projeto em {destination}", [projects]); Assert.Equal("UnsafeDestination", boundary.Initialize(destination).Code); }
        finally { Directory.Delete(link); Directory.Delete(outside, true); }
    }

    [Fact] public async Task CreateDirectoryCannotEscapeCurrentWorkspace()
    { var result = await new ToolExecutor().ExecuteAsync(initial, new(ToolNames.CreateDirectory, new() { ["path"]="../escape" }, null), false, default); Assert.False(result.Success); }

    [Fact] public async Task NewProjectWithoutGitUsesIsolatedFileManifestAsDiffEvidence()
    { Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target,"index.html"),"<html></html>"); var result = await new ToolExecutor().ExecuteAsync(target, new(ToolNames.GitDiff, [], null), true, default); Assert.True(result.Success); Assert.Equal("true", result.Metadata["has_changes"]); Assert.Equal("unversioned-new-project", result.Metadata["workspace_mode"]); Assert.Contains("index.html", result.Output); }

    [Fact] public async Task PathOutsideWorkspaceRecoversIntoExplicitNewProjectWithoutFourRepeats()
    {
        var model = new QueueModel(
            $"{{\"type\":\"action\",\"action\":\"write_file\",\"arguments\":{{\"path\":\"{target}/index.html\",\"content\":\"<html></html>\"}}}}",
            "{\"type\":\"action\",\"action\":\"list_files\",\"arguments\":{\"path\":\".\"}}");
        var result = await new AgentOrchestrator(model, new ToolExecutor(), new NullDataset(), maxSteps: 2).RunAsync(initial, $"Crie um novo projeto em {target}", new(true,false,false), default);
        Assert.True(File.Exists(Path.Combine(target, "index.html"))); Assert.Equal(target, result.WorkspaceRoot); Assert.Equal(2, model.Calls);
    }

    public void Dispose() { var root = Directory.GetParent(projects)!.FullName; if (Directory.Exists(root)) Directory.Delete(root, true); }
    sealed class QueueModel(params string[] values) : IModelAdapter { readonly Queue<string> queue = new(values); public int Calls { get; private set; } public string ModelId => "workspace-test"; public Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) { Calls++; return Task.FromResult(queue.Dequeue()); } }
    sealed class NullDataset : IDatasetService { public Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct) => Task.CompletedTask; }
}
