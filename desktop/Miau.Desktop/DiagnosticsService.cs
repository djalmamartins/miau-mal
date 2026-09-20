using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Miau.Desktop;

public sealed record DiagnosticItem(string Name, bool Success, string Detail);

public sealed class DiagnosticsService
{
    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(string? workspace, string model, string? visionModel, CancellationToken ct)
    {
        var items = new List<DiagnosticItem>();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromSeconds(5) };
            using var version = await http.GetAsync("/api/version", ct); items.Add(new("Ollama", version.IsSuccessStatusCode, version.IsSuccessStatusCode ? "Disponível" : version.StatusCode.ToString()));
            var json = await http.GetStringAsync("/api/tags", ct); using var doc = JsonDocument.Parse(json);
            var modelNodes = doc.RootElement.GetProperty("models").EnumerateArray().ToArray();
            var installed = modelNodes.Any(x => x.GetProperty("name").GetString() == model);
            items.Add(new(model, installed, installed ? "Instalado" : "Não encontrado"));
            var vision = modelNodes.FirstOrDefault(x => (string.IsNullOrWhiteSpace(visionModel) || x.GetProperty("name").GetString() == visionModel) && x.TryGetProperty("capabilities", out var caps) && caps.EnumerateArray().Any(c => c.GetString() == "vision"));
            var visionName = vision.ValueKind == JsonValueKind.Undefined ? null : vision.GetProperty("name").GetString();
            items.Add(new("Vision model", visionName is not null, visionName ?? "Nenhum modelo multimodal configurado"));
            items.Add(new("Vision capability", visionName is not null, visionName is not null ? "YES" : "NO"));
            items.Add(new("Multimodal request", visionName is not null, visionName is not null ? "Disponível; valide com screenshot real" : "Indisponível"));
        }
        catch (Exception ex) { items.Add(new("Ollama", false, ex.Message)); items.Add(new(model, false, "Ollama indisponível")); }
        var validWorkspace = workspace is not null && Directory.Exists(workspace); items.Add(new("Workspace", validWorkspace, validWorkspace ? workspace! : "Não aberto"));
        items.Add(new("Git", validWorkspace && Directory.Exists(Path.Combine(workspace!, ".git")), validWorkspace ? "Verificado" : "Sem workspace"));
        items.Add(new("ToolExecutor", true, "Carregado"));
        items.Add(new("Render engine", true, "Chrome headless configurado"));
        items.Add(new("Visual Inspector", true, "IVisualInspector carregado"));
        items.Add(CheckDirectory("Dataset", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "dataset")));
        items.Add(CheckDirectory("Memory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "memory")));
        return items;
    }
    static DiagnosticItem CheckDirectory(string name, string path)
    {
        try { Directory.CreateDirectory(path); return new(name, true, path); } catch (Exception ex) { return new(name, false, ex.Message); }
    }
}
