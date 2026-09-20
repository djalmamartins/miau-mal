using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miau.Desktop;

public enum ModelCapability { TextOnly, Coding, Vision, Unknown }
public enum VisionStatus { Ready, Unavailable }
public enum VisualVerdict { Approved, NeedsRevision, Unavailable }
public enum VisualIssueCategory { Layout, Overflow, Spacing, Typography, Contrast, MissingAsset, Alignment, Responsiveness, Navigation, Hierarchy, Readability, BrokenElement, Other }
public enum VisualSeverity { Low, Medium, High, Critical }

public sealed record VisualViewport(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
    public static VisualViewport Parse(string value)
    {
        var parts = value.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height)
            ? new(width, height) : new(0, 0);
    }
}

public sealed record VisualIssue(string Id, VisualIssueCategory Category, string Problem, string Evidence,
    string Location, VisualSeverity Severity, double Confidence, string SuggestedAction);

public sealed record VisualInspectionRequest(string ScreenshotPath, VisualViewport Viewport, string TaskObjective,
    IReadOnlyList<string> VisualCriteria, VisualInspectionResult? PreviousInspection = null, string? PreviousScreenshotPath = null);

public sealed record VisualInspectionResult(VisionStatus Status, VisualVerdict Verdict, IReadOnlyList<VisualIssue> Issues,
    string Provider, string? Model, string ScreenshotHash, VisualViewport Viewport, DateTimeOffset StartedAt,
    TimeSpan Duration, bool ImageIncluded, string? Error = null)
{
    public bool IsRealEvidence => Status == VisionStatus.Ready && ImageIncluded && !string.IsNullOrWhiteSpace(Model) && ScreenshotHash.Length == 64;
}

public interface IVisualInspector
{
    Task<VisualInspectionResult> InspectAsync(VisualInspectionRequest request, CancellationToken ct);
}

public sealed record LocalModelInfo(string Name, ModelCapability Capability);

public sealed class OllamaCapabilityDiscovery(HttpClient? client = null)
{
    readonly HttpClient http = client ?? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromSeconds(8) };

    public async Task<IReadOnlyList<LocalModelInfo>> DiscoverAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/api/tags", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var models = new List<LocalModelInfo>();
        foreach (var item in document.RootElement.GetProperty("models").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var capabilities = item.TryGetProperty("capabilities", out var caps)
                ? caps.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            var capability = capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase) ? ModelCapability.Vision
                : capabilities.Contains("completion", StringComparer.OrdinalIgnoreCase) && name.Contains("coder", StringComparison.OrdinalIgnoreCase) ? ModelCapability.Coding
                : capabilities.Contains("completion", StringComparer.OrdinalIgnoreCase) ? ModelCapability.TextOnly : ModelCapability.Unknown;
            models.Add(new(name, capability));
        }
        return models;
    }
}

public sealed class OllamaVisualInspector(string? configuredModel = null, HttpClient? client = null) : IVisualInspector
{
    readonly HttpClient http = client ?? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = Timeout.InfiniteTimeSpan };
    readonly string? configured = configuredModel;

    public async Task<VisualInspectionResult> InspectAsync(VisualInspectionRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.ScreenshotPath) || !File.Exists(request.ScreenshotPath))
            return Unavailable("Screenshot não encontrado.", started, watch.Elapsed, request.Viewport);
        var extension = Path.GetExtension(request.ScreenshotPath);
        if (!new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Unavailable("Formato de screenshot não suportado.", started, watch.Elapsed, request.Viewport);
        var pixels = await File.ReadAllBytesAsync(request.ScreenshotPath, ct);
        if (pixels.Length == 0) return Unavailable("Screenshot vazio.", started, watch.Elapsed, request.Viewport);
        var hash = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        IReadOnlyList<LocalModelInfo> models;
        try { models = await new OllamaCapabilityDiscovery(http).DiscoverAsync(ct); }
        catch (Exception ex) { return Unavailable("Ollama indisponível: " + ex.Message, started, watch.Elapsed, request.Viewport, hash); }
        var selected = string.IsNullOrWhiteSpace(configured)
            ? models.FirstOrDefault(x => x.Capability == ModelCapability.Vision)
            : models.FirstOrDefault(x => x.Name.Equals(configured, StringComparison.OrdinalIgnoreCase));
        if (selected is null || selected.Capability != ModelCapability.Vision)
            return Unavailable("Inspeção visual real indisponível: nenhum modelo multimodal configurado.", started, watch.Elapsed, request.Viewport, hash, configured);

        var prompt = BuildPrompt(request);
        var payload = new
        {
            model = selected.Name, stream = false, format = "json",
            messages = new[] { new { role = "user", content = prompt, images = new[] { Convert.ToBase64String(pixels) } } },
            options = new { temperature = 0.0 }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await http.PostAsJsonAsync("/api/chat", payload, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) return Unavailable($"Ollama visual respondeu HTTP {(int)response.StatusCode}.", started, watch.Elapsed, request.Viewport, hash, selected.Name);
            using var envelope = JsonDocument.Parse(body);
            var content = envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            if (!TryParse(content, out var verdict, out var issues, out var error))
                return Unavailable("Resposta visual estruturada inválida: " + error + " Resposta: " + (content.Length > 1200 ? content[..1200] : content), started, watch.Elapsed, request.Viewport, hash, selected.Name);
            return new(VisionStatus.Ready, verdict, issues, "ollama", selected.Name, hash, request.Viewport, started, watch.Elapsed, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Unavailable("Tempo limite na inspeção visual.", started, watch.Elapsed, request.Viewport, hash, selected.Name); }
        catch (Exception ex) { return Unavailable("Falha na inspeção visual: " + ex.Message, started, watch.Elapsed, request.Viewport, hash, selected.Name); }
    }

    static string BuildPrompt(VisualInspectionRequest request) => $$"""
Você é o sistema de inspeção visual do MIAU. Analise SOMENTE os pixels do screenshot fornecido.
Não analise código-fonte, não programe, não invente elementos e não use opiniões genéricas.
Identifique apenas problemas concretos e visualmente observáveis de layout, overflow, espaçamento, tipografia, contraste, assets ausentes, alinhamento, responsividade, navegação, hierarquia, legibilidade ou elementos quebrados.
Viewport: {{request.Viewport}}. Objetivo: {{request.TaskObjective}}. Critérios: {{string.Join("; ", request.VisualCriteria)}}.
Retorne SOMENTE JSON: {"verdict":"Approved|NeedsRevision","issues":[{"id":"...","category":"Layout|Overflow|Spacing|Typography|Contrast|MissingAsset|Alignment|Responsiveness|Navigation|Hierarchy|Readability|BrokenElement|Other","problem":"...","evidence":"...","location":"...","severity":"Low|Medium|High|Critical","confidence":0.0,"suggestedAction":"..."}]}
Use Approved com issues vazio quando não houver problema observável. Cada issue deve conter evidência, local e ação concreta.
""";

    public static bool TryParse(string json, out VisualVerdict verdict, out IReadOnlyList<VisualIssue> issues, out string error)
    {
        verdict = VisualVerdict.Unavailable; issues = []; error = "JSON inválido";
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (!Enum.TryParse<VisualVerdict>(root.GetProperty("verdict").GetString(), true, out verdict) || verdict == VisualVerdict.Unavailable) { error = "verdict inválido"; return false; }
            var parsed = new List<VisualIssue>();
            foreach (var item in root.GetProperty("issues").EnumerateArray())
            {
                var problem = TextAny(item, "problem", "problema"); var evidence = TextAny(item, "evidence", "evidencia", "evidência"); var location = TextAny(item, "location", "local"); var action = TextAny(item, "suggestedAction", "suggested_action", "acao_sugerida", "ação_sugerida");
                if (new[] { problem, evidence, location, action }.Any(string.IsNullOrWhiteSpace) || IsGeneric(problem)) { error = "issue genérica ou incompleta"; return false; }
                Enum.TryParse<VisualIssueCategory>(Text(item, "category"), true, out var category);
                Enum.TryParse<VisualSeverity>(Text(item, "severity"), true, out var severity);
                var confidence = item.TryGetProperty("confidence", out var confidenceNode) && confidenceNode.TryGetDouble(out var number) ? Math.Clamp(number, 0, 1) : 0;
                parsed.Add(new(Text(item, "id"), category, problem, evidence, location, severity, confidence, action));
            }
            if (verdict == VisualVerdict.NeedsRevision && parsed.Count == 0) { error = "revisão sem issues"; return false; }
            if (verdict == VisualVerdict.Approved && parsed.Count != 0) { error = "aprovação contraditória com issues"; return false; }
            issues = parsed; error = ""; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    static bool IsGeneric(string value) => new[] { "está feio", "poderia melhorar", "não parece profissional" }.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
    static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var node) ? node.GetString() ?? "" : "";
    static string TextAny(JsonElement element, params string[] names) { foreach (var name in names) { var value = Text(element, name); if (!string.IsNullOrWhiteSpace(value)) return value; } return ""; }
    static VisualInspectionResult Unavailable(string error, DateTimeOffset started, TimeSpan duration, VisualViewport viewport, string hash = "", string? model = null) =>
        new(VisionStatus.Unavailable, VisualVerdict.Unavailable, [], "ollama", model, hash, viewport, started, duration, false, error);
}

public sealed class FakeVisualInspector(Func<VisualInspectionRequest, VisualInspectionResult> result) : IVisualInspector
{
    public int Calls { get; private set; }
    public Task<VisualInspectionResult> InspectAsync(VisualInspectionRequest request, CancellationToken ct) { Calls++; return Task.FromResult(result(request)); }
}
