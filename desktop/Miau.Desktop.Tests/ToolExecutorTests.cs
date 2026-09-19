using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ToolExecutorTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-tools-" + Guid.NewGuid().ToString("N"));
    readonly ToolExecutor tools = new();
    public ToolExecutorTests() => Directory.CreateDirectory(root);
    [Fact] public async Task PathTraversalIsBlocked() { var r = await tools.ExecuteAsync(root, new(ToolNames.ReadFile, new() { ["path"] = "../secret.txt" }, null), false, default); Assert.False(r.Success); Assert.Contains("fora", r.Error); }
    [Fact] public async Task WriteOutsideWorkspaceIsBlocked() { var r = await tools.ExecuteAsync(root, new(ToolNames.WriteFile, new() { ["path"] = "../escape.txt", ["content"] = "x" }, null), false, default); Assert.False(r.Success); }
    [Theory] [InlineData("git reset --hard HEAD")] [InlineData("rm -rf build")] [InlineData("git push --force origin main")]
    public async Task DestructiveCommandsAreBlocked(string command) { var r = await tools.ExecuteAsync(root, new(ToolNames.RunCommand, new() { ["command"] = command }, null), false, default); Assert.False(r.Success); Assert.Contains("bloqueado", r.Error); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
