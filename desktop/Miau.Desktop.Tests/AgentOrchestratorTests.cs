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
        var timeline = new List<ExecutionEvent>();
        var result = await new AgentOrchestrator(model, tools, dataset).RunAsync("/workspace", "Adicione um tooltip mais descritivo ao botão Atualizar diff. Faça somente essa alteração.", new(true, false), CancellationToken.None, eventSink: timeline.Add);
        Assert.Equal(JobPhase.Completed, result.Phase);
        Assert.Contains(ToolNames.ReadFile, tools.Calls);
        Assert.Contains(ToolNames.ReplaceInFile, tools.Calls);
        Assert.Contains(ToolNames.GitDiff, tools.Calls);
        Assert.True(tools.Validated);
        Assert.NotNull(dataset.Record);
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.ModelRequestStarted);
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.FileChanged || x.Description == "Alterou arquivo");
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.DiffCompleted);
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.JobCompleted);
    }

    [Fact]
    public async Task CancellationCancelsModelAndEmitsCancelled()
    {
        using var cts = new CancellationTokenSource(30); var timeline = new List<ExecutionEvent>();
        var orchestrator = new AgentOrchestrator(new BlockingModel(), new RecordingTools(), new RecordingDataset());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.RunAsync("/workspace", "analise", new(false, true), cts.Token, eventSink: timeline.Add));
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.JobCancelled && x.Phase == JobPhase.Cancelled);
    }

    [Fact]
    public async Task BuildFailureTriggersRetryAndThenCompletes()
    {
        var model = new QueueModel(
            """{"type":"action","action":"read_file","arguments":{"path":"a.cs"}}""",
            """{"type":"action","action":"replace_in_file","arguments":{"path":"a.cs","old_text":"a","new_text":"b"}}""",
            """{"type":"final","summary":"primeira tentativa","files_changed":["a.cs"]}""",
            """{"type":"action","action":"replace_in_file","arguments":{"path":"a.cs","old_text":"b","new_text":"c"}}""",
            """{"type":"final","summary":"corrigido","files_changed":["a.cs"]}""");
        var tools = new RecordingTools { FailFirstValidation = true }; var timeline = new List<ExecutionEvent>();
        var result = await new AgentOrchestrator(model, tools, new RecordingDataset()).RunAsync("/workspace", "corrija a.cs", new(true, false), default, eventSink: timeline.Add);
        Assert.Equal(JobPhase.Completed, result.Phase); Assert.Equal(2, tools.ValidationCount);
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.BuildFailed);
        Assert.Contains(timeline, x => x.Type == ExecutionEventType.RetryStarted);
    }

    [Fact]
    public async Task DatasetIsNotSavedWhenJobFails()
    {
        var dataset = new RecordingDataset();
        var result = await new AgentOrchestrator(new QueueModel("prosa", "prosa", "prosa", "prosa"), new RecordingTools(), dataset)
            .RunAsync("/workspace", "edite algo", new(true, false), default);
        Assert.Equal(JobPhase.Failed, result.Phase); Assert.Null(dataset.Record);
    }

    [Theory]
    [InlineData("melhore significativamente o site", true)]
    [InlineData("faça um redesign da landing page", true)]
    [InlineData("crie header, hero, footer e layout responsivo", true)]
    [InlineData("corrija o texto do botão", false)]
    public void BroadVisualRewriteClassificationIsDeterministic(string task, bool expected)
        => Assert.Equal(expected, AgentOrchestrator.IsBroadVisualRewrite(task));

    [Fact]
    public void BroadVisualEvidenceRejectsSingleFileTweak()
    {
        var oneFile = new JobEvidence(["index.html"], ["index.html"], true, true, true, 0, null, true, true, true);
        var twoFiles = new JobEvidence(["index.html", "style.css"], ["index.html", "style.css"], true, true, true, 0, null, true, true, true);
        Assert.False(AgentOrchestrator.HasBroadVisualEvidence(oneFile));
        Assert.True(AgentOrchestrator.HasBroadVisualEvidence(twoFiles));
    }

    [Fact]
    public void VisualAcceptanceReportsMissingRequestedSections()
    {
        var task = "site com header, hero, produtos em destaque, seção editorial, chamadas, footer completo e responsivo";
        var result = VisualAcceptance.Evaluate(task, "<header></header><section class=\"hero\"></section><section class=\"products\"></section><footer></footer>", "body{}");
        Assert.False(result.Passed);
        Assert.Contains("seção editorial", result.Missing);
        Assert.Contains("chamadas/CTA", result.Missing);
        Assert.Contains("responsividade CSS (@media/@container)", result.Missing);
    }

    [Fact]
    public void VisualAcceptancePassesWhenRequestedEvidenceExists()
    {
        var task = "site com header, hero, produtos em destaque, seção editorial, chamadas, footer completo e responsivo";
        var html = "<meta name=\"viewport\" content=\"width=device-width\"><header></header><section class=\"hero\"></section><section class=\"products\"></section><section class=\"editorial\"></section><a class=\"cta\">Ver menu</a><footer></footer>";
        var result = VisualAcceptance.Evaluate(task, html, "@media (max-width: 700px) { body { display:block; } }");
        Assert.True(result.Passed);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void JobEngineDoesNotDependOnModelAdapter()
    {
        var engine = new JobEngine(new(false, true)); engine.Start();
        Assert.Equal(JobPhase.Understanding, engine.Phase);
        Assert.DoesNotContain(typeof(JobEngine).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), x => typeof(IModelAdapter).IsAssignableFrom(x.FieldType));
    }

    sealed class QueueModel(params string[] responses) : IModelAdapter
    {
        readonly Queue<string> queue = new(responses); public string ModelId => "fake-coder";
        public Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) => Task.FromResult(queue.Dequeue());
    }
    sealed class RecordingTools : IToolExecutor
    {
        public List<string> Calls { get; } = []; public bool Validated { get; private set; }
        public bool FailFirstValidation { get; init; }
        public int ValidationCount { get; private set; }
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
        {
            Validated = true; ValidationCount++;
            if (FailFirstValidation && ValidationCount == 1) return Task.FromResult(ToolResult.Fail(ToolNames.Build, "compile error"));
            return Task.FromResult(ToolResult.Ok(ToolNames.Build, "Build succeeded", ("validation", "true")));
        }
    }
    sealed class BlockingModel : IModelAdapter
    {
        public string ModelId => "blocking";
        public async Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ""; }
    }
    sealed class RecordingDataset : IDatasetService
    {
        public TrainingRecord? Record { get; private set; }
        public Task SaveCompletedAsync(string workspace, TrainingRecord record, CancellationToken ct) { Record = record; return Task.CompletedTask; }
    }
}
