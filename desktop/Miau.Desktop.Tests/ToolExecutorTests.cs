using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ToolExecutorTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-tools-" + Guid.NewGuid().ToString("N"));
    readonly ToolExecutor tools = new();
    public ToolExecutorTests() => Directory.CreateDirectory(root);
    [Fact] public async Task WorkspaceRootDotIsAllowed()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "inside.txt"), "ok");
        var r = await tools.ExecuteAsync(root + Path.DirectorySeparatorChar, new(ToolNames.ListFiles, new() { ["path"] = "." }, null), false, default);
        Assert.True(r.Success, r.Error);
        Assert.Contains("inside.txt", r.Output);
    }

    [Fact] public async Task PathTraversalIsBlocked() { var r = await tools.ExecuteAsync(root, new(ToolNames.ReadFile, new() { ["path"] = "../secret.txt" }, null), false, default); Assert.False(r.Success); Assert.Contains("fora", r.Error); }
    [Fact] public async Task WriteOutsideWorkspaceIsBlocked() { var r = await tools.ExecuteAsync(root, new(ToolNames.WriteFile, new() { ["path"] = "../escape.txt", ["content"] = "x" }, null), false, default); Assert.False(r.Success); }
    [Fact] public async Task IdenticalWriteIsNotProgress()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "same.txt"), "same");
        var r = await tools.ExecuteAsync(root, new(ToolNames.WriteFile, new() { ["path"] = "same.txt", ["content"] = "same" }, null), false, default);
        Assert.True(r.Success); Assert.Equal("false", r.Metadata["effective_change"]); Assert.False(r.Metadata.ContainsKey("changed_path")); Assert.Contains("NoEffectiveChange", r.Output);
    }
    [Fact] public async Task IdenticalReplacementIsNotProgress()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "same.txt"), "same");
        var r = await tools.ExecuteAsync(root, new(ToolNames.ReplaceInFile, new() { ["path"] = "same.txt", ["old_text"] = "same", ["new_text"] = "same" }, null), false, default);
        Assert.True(r.Success); Assert.Equal("false", r.Metadata["effective_change"]); Assert.False(r.Metadata.ContainsKey("changed_path"));
    }
    [Fact] public void IdenticalPatchIsNotProgress()
    {
        var r = ToolExecutor.PatchResult(ToolNames.ApplyPatch, "", []);
        Assert.True(r.Success); Assert.Equal("false", r.Metadata["effective_change"]); Assert.False(r.Metadata.ContainsKey("changed_path"));
    }
    [Fact] public async Task SymlinkOutsideWorkspaceIsBlocked()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "miau-outside-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
            var r = await tools.ExecuteAsync(root, new(ToolNames.WriteFile, new() { ["path"] = "escape/file.txt", ["content"] = "x" }, null), false, default);
            Assert.False(r.Success); Assert.Contains("simbólico", r.Error); Assert.False(File.Exists(Path.Combine(outside, "file.txt")));
        }
        finally { Directory.Delete(outside, true); }
    }
    [Fact] public async Task Reference403DoesNotAbortOptionalTask()
    {
        var executor = new ToolExecutor(new FakeReferences(new(false, Reason: "HTTP 403")));
        var r = await executor.ExecuteAsync(root, new(ToolNames.FetchUrl, new() { ["url"] = "https://example.com/reference" }, null), false, default);
        Assert.True(r.Success); Assert.Equal("unavailable", r.Metadata["reference_status"]); Assert.Equal("false", r.Metadata["reference_analyzed"]);
    }
    [Fact] public async Task ReferenceFailureCannotBeReportedAsAnalyzed()
    {
        var executor = new ToolExecutor(new FakeReferences(new(false, Reason: "timeout")));
        var r = await executor.ExecuteAsync(root, new(ToolNames.FetchUrl, new() { ["url"] = "https://example.com/reference" }, null), false, default);
        Assert.Contains("NÃO foi analisado", r.Output); Assert.DoesNotContain("conteúdo analisado", r.Output, StringComparison.OrdinalIgnoreCase);
    }
    [Theory] [InlineData("http://10.0.0.1/")] [InlineData("http://192.168.1.1/")] [InlineData("http://169.254.169.254/")]
    public async Task PrivateNetworkReferencesAreBlocked(string url)
    {
        var r = await tools.ExecuteAsync(root, new(ToolNames.FetchUrl, new() { ["url"] = url }, null), false, default);
        Assert.False(r.Success);
    }
    [Theory] [InlineData("git reset --hard HEAD")] [InlineData("rm -rf build")] [InlineData("git push --force origin main")]
    public async Task DestructiveCommandsAreBlocked(string command) { var r = await tools.ExecuteAsync(root, new(ToolNames.RunCommand, new() { ["command"] = command }, null), false, default); Assert.False(r.Success); Assert.Contains("bloqueado", r.Error); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    sealed class FakeReferences(WebReferenceResult result) : IWebReferenceProvider
    {
        public Task<WebReferenceResult> FetchAsync(Uri uri, CancellationToken ct) => Task.FromResult(result);
    }
}
