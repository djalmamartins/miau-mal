using System.Diagnostics;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class OllamaIntegrationTests
{
    [Fact]
    [Trait("Category", "OllamaIntegration")]
    public async Task RealOllamaCompletesControlledTooltipTask()
    {
        if (Environment.GetEnvironmentVariable("MIAU_RUN_OLLAMA_INTEGRATION") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "miau-ollama-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Acceptance.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), "public static class Program { public static void Main() { } }");
            await File.WriteAllTextAsync(Path.Combine(root, "MainWindow.axaml"), "<Window><Button Content=\"Atualizar diff\" /></Window>");
            await Git(root, "init"); await Git(root, "add", "."); await Git(root, "-c", "user.name=MIAU", "-c", "user.email=miau@local", "commit", "-m", "baseline");
            var timeline = new List<ExecutionEvent>(); var dataset = new DatasetService(Path.Combine(root, ".dataset"));
            var result = await new AgentOrchestrator(new OllamaModelAdapter("qwen2.5-coder:7b", requestTimeout: TimeSpan.FromMinutes(3)), new ToolExecutor(), dataset, 20)
                .RunAsync(root, "Adicione um tooltip mais descritivo ao botão Atualizar diff. Faça somente essa alteração.", new(true, false), CancellationToken.None, eventSink: timeline.Add);
            var content = await File.ReadAllTextAsync(Path.Combine(root, "MainWindow.axaml"));
            var diagnostic = $"Phase={result.Phase}; Summary={result.Summary}; Conteúdo final: {content}\nTimeline: " + string.Join(" | ", timeline.Select(x => $"{x.Type}:{x.Target}:{x.Details}"));
            Assert.True(result.Phase == JobPhase.Completed, diagnostic);
            Assert.True(content.Contains("ToolTip", StringComparison.Ordinal), diagnostic);
            Assert.Contains(timeline, x => x.Type == ExecutionEventType.DiffCompleted);
            Assert.Contains(timeline, x => x.Type == ExecutionEventType.BuildCompleted);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static async Task Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
    }
}
