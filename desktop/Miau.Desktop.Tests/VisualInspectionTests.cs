using System.Net;
using System.Security.Cryptography;
using System.Text;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class VisualInspectionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "miau-vision-tests-" + Guid.NewGuid().ToString("N"));
    readonly string screenshot;
    readonly string hash;

    public VisualInspectionTests()
    {
        Directory.CreateDirectory(root); screenshot = Path.Combine(root, "screen.png");
        File.WriteAllBytes(screenshot, [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);
        hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshot))).ToLowerInvariant();
    }

    VisualInspectionResult Approved(VisualInspectionRequest request) => new(VisionStatus.Ready, VisualVerdict.Approved, [], "fake", "vision-test", hash, request.Viewport, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(3), true);
    ToolResult Render(string viewport, string value) => ToolResult.Ok(ToolNames.RenderPage, "render", ("visual_validation", "true"), ("viewport", viewport), ("screenshot_hash", value), ("screenshot_path", screenshot));
    ToolResult Inspect(string viewport, string value, string verdict = "approved") => ToolResult.Ok(ToolNames.InspectVisual, "evidence", ("visual_inspection", "true"), ("image_included", "true"), ("viewport", viewport), ("screenshot_hash", value), ("vision_provider", "fake"), ("vision_model", "vision-test"), ("visual_verdict", verdict), ("visual_issue_count", "0"));

    [Fact] public async Task VisualInspectionRequiresActualImage()
    {
        var fake = new FakeVisualInspector(Approved); var tool = new ToolExecutor(visualInspector: fake);
        var result = await tool.ExecuteAsync(root, new(ToolNames.InspectVisual, new() { ["screenshot_path"] = screenshot, ["viewport"] = "1440x1200" }, null), false, default);
        Assert.True(result.Success); Assert.Equal("true", result.Metadata["image_included"]); Assert.Equal(1, fake.Calls);
    }

    [Fact] public async Task MissingScreenshotCannotBeInspected()
    {
        var fake = new FakeVisualInspector(Approved); var result = await new ToolExecutor(visualInspector: fake).ExecuteAsync(root, new(ToolNames.InspectVisual, new() { ["screenshot_path"] = Path.Combine(root, "missing.png") }, null), false, default);
        Assert.False(result.Success); Assert.Equal(0, fake.Calls);
    }

    [Fact] public async Task TextOnlyModelCannotSatisfyVisualInspection()
    {
        var client = new HttpClient(new StaticHandler("{\"models\":[{\"name\":\"text:1\",\"capabilities\":[\"completion\"]}]}")) { BaseAddress = new Uri("http://localhost") };
        var result = await new OllamaVisualInspector("text:1", client).InspectAsync(new(screenshot, new(1440, 1200), "task", []), default);
        Assert.Equal(VisionStatus.Unavailable, result.Status); Assert.False(result.ImageIncluded);
    }

    [Fact] public async Task UnavailableVisionDoesNotPretendSuccess()
    {
        var unavailable = new FakeVisualInspector(r => new(VisionStatus.Unavailable, VisualVerdict.Unavailable, [], "ollama", null, hash, r.Viewport, DateTimeOffset.UtcNow, TimeSpan.Zero, false, "unavailable"));
        var result = await new ToolExecutor(visualInspector: unavailable).ExecuteAsync(root, new(ToolNames.InspectVisual, new() { ["screenshot_path"] = screenshot, ["viewport"] = "1440x1200" }, null), false, default);
        Assert.False(result.Success); Assert.Equal("false", result.Metadata["visual_inspection"]);
    }

    [Fact] public async Task VisualResultContainsScreenshotHash()
    {
        var result = await new ToolExecutor(visualInspector: new FakeVisualInspector(Approved)).ExecuteAsync(root, new(ToolNames.InspectVisual, new() { ["screenshot_path"] = screenshot, ["viewport"] = "1440x1200" }, null), false, default);
        Assert.Equal(hash, result.Metadata["screenshot_hash"]); Assert.Equal(64, result.Metadata["screenshot_hash"].Length);
    }

    [Fact] public void DesktopAndMobileHaveIndependentEvidence()
    {
        var job = new JobEngine(new(true, false, false, true), acceptance: AcceptancePlanner.Build("corrija site responsivo", new(true, false, false, true)));
        job.Observe(Render("1440x1200", hash)); job.Observe(Inspect("1440x1200", hash));
        Assert.Single(job.Evidence.VisualEvidence!);
        var mobileHash = new string('b', 64); job.Observe(Render("390x844", mobileHash)); job.Observe(Inspect("390x844", mobileHash));
        Assert.Equal(2, job.Evidence.VisualEvidence!.Count);
    }

    [Fact] public void VisualIssueIsStructured()
    {
        var json = "{\"verdict\":\"NeedsRevision\",\"issues\":[{\"id\":\"overflow-1\",\"category\":\"Overflow\",\"problem\":\"Menu cortado\",\"evidence\":\"Item sai da borda direita\",\"location\":\"Header\",\"severity\":\"High\",\"confidence\":0.9,\"suggestedAction\":\"Rever layout mobile\"}]}";
        Assert.True(OllamaVisualInspector.TryParse(json, out var verdict, out var issues, out _)); Assert.Equal(VisualVerdict.NeedsRevision, verdict); Assert.Equal(VisualIssueCategory.Overflow, Assert.Single(issues).Category);
    }

    [Fact] public void GenericVisualOpinionIsRejected()
    {
        var json = "{\"verdict\":\"NeedsRevision\",\"issues\":[{\"id\":\"x\",\"category\":\"Other\",\"problem\":\"Está feio\",\"evidence\":\"parece ruim\",\"location\":\"página\",\"severity\":\"Low\",\"confidence\":0.5,\"suggestedAction\":\"melhorar\"}]}";
        Assert.False(OllamaVisualInspector.TryParse(json, out _, out _, out _));
    }

    [Fact] public void ApprovedVerdictWithIssuesIsRejected()
    {
        var json = "{\"verdict\":\"Approved\",\"issues\":[{\"id\":\"x\",\"category\":\"Layout\",\"problem\":\"Footer sobrepõe CTA\",\"evidence\":\"CTA coberto\",\"location\":\"rodapé\",\"severity\":\"High\",\"confidence\":0.9,\"suggested_action\":\"Remover position fixed\"}]}";
        Assert.False(OllamaVisualInspector.TryParse(json, out _, out _, out var error)); Assert.Contains("contraditória", error);
    }

    [Fact] public void IdenticalScreenshotDoesNotCreateVisualProgress()
    { var tracker = new ProgressTracker(); Assert.True(tracker.Record(ProgressKind.Visual, hash + "|390x844|criteria")); Assert.False(tracker.Record(ProgressKind.Visual, hash + "|390x844|criteria")); }

    [Fact] public void ChangedScreenshotCanCreateVisualProgress()
    { var tracker = new ProgressTracker(); tracker.Record(ProgressKind.Visual, hash + "|390x844|criteria"); Assert.True(tracker.Record(ProgressKind.Visual, new string('c', 64) + "|390x844|criteria")); }

    [Fact] public void VisualRevisionIsBounded()
    { var policy = new VisualRevisionPolicy(2); Assert.True(policy.CanRevise("a", "1", out _)); Assert.True(policy.CanRevise("b", "1", out _)); Assert.False(policy.CanRevise("c", "1", out var reason)); Assert.Contains("Limite", reason); }

    [Fact] public void CompletionGateRequiresRealVisualEvidence()
    { var job = new JobEngine(new(true, false, true, true)); job.Observe(Render("1440x1200", hash)); job.Observe(ToolResult.Ok(ToolNames.InspectVisual, "claimed", ("visual_inspection", "true"), ("visual_verdict", "approved"))); Assert.False(job.TryComplete(out _)); Assert.False(job.VisualInspectionRan); }

    [Fact] public void CodingModelDoesNotSelfApproveVisualInspection()
    { var job = new JobEngine(new(true, false, false, true)); job.Observe(Render("1440x1200", hash)); Assert.False(job.VisualInspectionRan); Assert.False(job.TryComplete(out _)); }

    [Fact] public async Task FakeVisualInspectorSupportsDeterministicTests()
    { var fake = new FakeVisualInspector(Approved); await fake.InspectAsync(new(screenshot, new(1440, 1200), "task", []), default); Assert.Equal(1, fake.Calls); }

    public void Dispose() => Directory.Delete(root, true);

    sealed class StaticHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
