using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ExecutionLogRegressionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-log-regression-" + Guid.NewGuid().ToString("N"));
    public ExecutionLogRegressionTests() => Directory.CreateDirectory(root);

    [Fact] public void LinkedNestedStylesheetIsUsedForVisualAcceptance()
    {
        Directory.CreateDirectory(Path.Combine(root,"css")); File.WriteAllText(Path.Combine(root,"css","style.css"),"@media(max-width:700px){}");
        var html = "<link rel='stylesheet' href='css/style.css'>";
        Assert.Equal("css/style.css", AgentOrchestrator.ResolveStylesheetPath(root, "index.html", html));
        Assert.True(VisualAcceptance.Evaluate("site responsivo", "<meta name='viewport'>", File.ReadAllText(Path.Combine(root,"css","style.css"))).Passed);
    }

    [Fact] public async Task RepeatedRenderActionsAreExecutedAndNotPreemptivelyBlocked()
    {
        var tools = new RenderCountingTools(); var model = new QueueModel(RenderAction(), RenderAction());
        await new AgentOrchestrator(model, tools, new NullDataset(), maxSteps:2).RunAsync(root,"renderize",new(false,true,false,false),default);
        Assert.Equal(2, tools.RenderCalls);
    }

    [Fact] public async Task ExplicitNamedProjectIsSelectedBeforeInitialListing()
    {
        var projects = Path.Combine(root,"Projects"); var initial = Path.Combine(projects,"miau","meu_projeto"); var target = Path.Combine(projects,"miau","site-teste"); Directory.CreateDirectory(initial); Directory.CreateDirectory(target);
        var tools = new CaptureWorkspaceTools();
        await new AgentOrchestrator(new QueueModel("{\"type\":\"final\",\"summary\":\"analisado\",\"files_changed\":[]}"),tools,new NullDataset(),1).RunAsync(initial,"Inspecione e melhore o projeto site-teste",new(false,true,false),default);
        Assert.Equal(target, tools.FirstWorkspace);
    }

    static string RenderAction() => "{\"type\":\"action\",\"action\":\"render_page\",\"arguments\":{\"path\":\"index.html\",\"width\":\"1440\",\"height\":\"1200\"}}";
    public void Dispose() { if(Directory.Exists(root)) Directory.Delete(root,true); }
    sealed class QueueModel(params string[] values) : IModelAdapter { readonly Queue<string> queue=new(values); public string ModelId=>"fake"; public Task<string> CompleteStepAsync(ModelRequest request,CancellationToken ct)=>Task.FromResult(queue.Dequeue()); }
    sealed class NullDataset : IDatasetService { public Task SaveCompletedAsync(string workspace,TrainingRecord record,CancellationToken ct)=>Task.CompletedTask; }
    sealed class RenderCountingTools : IToolExecutor
    {
        public int RenderCalls { get; private set; }
        public Task<ToolResult> ExecuteAsync(string workspace,MiauAction action,bool readOnly,CancellationToken ct) { if(action.Action==ToolNames.RenderPage) RenderCalls++; return Task.FromResult(action.Action==ToolNames.RenderPage ? ToolResult.Ok(action.Action,"render",("visual_validation","true"),("viewport","1440x1200"),("screenshot_hash",new string((char)('a'+RenderCalls),64))) : ToolResult.Ok(action.Action,"files",("inspected_path","."))); }
        public Task<ToolResult> ValidateAsync(string workspace,CancellationToken ct)=>Task.FromResult(ToolResult.Ok(ToolNames.Test,"ok",("validation","true")));
    }
    sealed class CaptureWorkspaceTools : IToolExecutor
    {
        public string? FirstWorkspace { get; private set; }
        public Task<ToolResult> ExecuteAsync(string workspace,MiauAction action,bool readOnly,CancellationToken ct) { FirstWorkspace ??= workspace; return Task.FromResult(ToolResult.Ok(action.Action,"files",("inspected_path","."))); }
        public Task<ToolResult> ValidateAsync(string workspace,CancellationToken ct)=>Task.FromResult(ToolResult.Ok(ToolNames.Test,"ok",("validation","true")));
    }
}
