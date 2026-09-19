using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class AgentOrchestratorTests
{
    [Fact]
    public async Task TooltipAcceptanceFlowRequiresDiffAndBuild()
    {
        var model = new QueueModel(
            """{"type":"plan","steps":["ler a interface","editar o tooltip"]}""",
            """{"type":"action","action":"read_file","arguments":{"path":"desktop/Miau.Desktop/MainWindow.axaml"},"reason":"localizar o botão"}""",
            """{"type":"action","action":"replace_in_file","arguments":{"path":"desktop/Miau.Desktop/MainWindow.axaml","old_text":"Atualizar diff","new_text":"Atualizar diff"},"reason":"adicionar tooltip"}""",
            """{"type":"final","summary":"Tooltip atualizado e build validado.","files_changed":["desktop/Miau.Desktop/MainWindow.axaml"]}""");
        var tools = new RecordingTools(); var dataset = new RecordingDataset();
        var result = await new AgentOrchestrator(model, tools, dataset).RunAsync("/workspace", "Adicione um tooltip mais descritivo ao botão Atualizar diff. Faça somente essa alteração.", new(true, false), CancellationToken.None);
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Contains(ToolNames.ReadFile, tools.Calls);
        Assert.Contains(ToolNames.ReplaceInFile, tools.Calls);
        Assert.Contains(ToolNames.GitDiff, tools.Calls);
        Assert.True(tools.Validated);
        Assert.NotNull(dataset.Record);
    }

    sealed class QueueModel(params string[] responses) : IModelAdapter
    {
        readonly Queue<string> queue = new(responses); public string ModelId => "fake-coder";
        public Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) => Task.FromResult(queue.Dequeue());
    }
    sealed class RecordingTools : IToolExecutor
    {
        public List<string> Calls { get; } = []; public bool Validated { get; private set; }
        public Task<ToolResult> ExecuteAsync(string workspace, MiauAction action, bool readOnly, CancellationToken ct)
        {
            Calls.Add(action.Action);
            var result = action.Action switch
            {
                ToolNames.ListFiles => ToolResult.Ok(action.Action, "MainWindow.axaml", ("inspected_path", ".")),
                ToolNames.ReadFile => ToolResult.Ok(action.Action, "<Button />", ("inspected_path", action.Arguments["path"])),
                ToolNames.ReplaceInFile => ToolResult.Ok(action.Action, "alterado", ("changed_path", action.Arguments["path"])),
                ToolNames.GitDiff => ToolResult.Ok(action.Action, "+ ToolTip.Tip", ("has_changes", "true")),
                _ => ToolResult.Fail(action.Action, "inesperado")
            };
            return Task.FromResult(result);
        }
        public Task<ToolResult> ValidateAsync(string workspace, CancellationToken ct)
        { Validated = true; return Task.FromResult(ToolResult.Ok(ToolNames.Build, "Build succeeded", ("validation", "true"))); }
    }
    sealed class RecordingDataset : IDatasetService
    {
        public TrainingRecord? Record { get; private set; }
        public Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct) { Record = record; return Task.CompletedTask; }
    }
}
