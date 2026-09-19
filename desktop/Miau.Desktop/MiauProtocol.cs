using System.Text.Json;

namespace Miau.Desktop;

public static class ToolNames
{
    public const string ListFiles = "list_files", ReadFile = "read_file", Search = "search", WriteFile = "write_file",
        ReplaceInFile = "replace_in_file", DeleteFile = "delete_file", ApplyPatch = "apply_patch", GitStatus = "git_status", GitDiff = "git_diff",
        Build = "build", Test = "test", RunCommand = "run_command", FetchUrl = "fetch_url", RenderPage = "render_page";
    public static readonly HashSet<string> All = [ListFiles, ReadFile, Search, WriteFile, ReplaceInFile, DeleteFile, ApplyPatch, GitStatus, GitDiff, Build, Test, RunCommand, FetchUrl, RenderPage];
}

public sealed record MiauAction(string Action, Dictionary<string, string> Arguments, string? Reason);
public sealed record MiauPlan(IReadOnlyList<string> Steps);
public sealed record MiauFinal(string Summary, IReadOnlyList<string> FilesChanged);
public sealed record BrainResponse(string Type, MiauAction? Action, MiauPlan? Plan, MiauFinal? Final)
{
    public static bool TryParse(string json, out BrainResponse? response, out string error)
    {
        response = null; error = "";
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement)) { error = "Resposta sem campo type."; return false; }
            switch (typeElement.GetString())
            {
                case "action":
                    if (!root.TryGetProperty("action", out var actionElement)) { error = "Ação ausente."; return false; }
                    var action = actionElement.GetString() ?? ""; if (!ToolNames.All.Contains(action)) { error = $"Ação desconhecida: {action}."; return false; }
                    var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
                        foreach (var item in args.EnumerateObject()) arguments[item.Name] = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() ?? "" : item.Value.GetRawText();
                    response = new("action", new(action, arguments, root.TryGetProperty("reason", out var r) ? r.GetString() : null), null, null); return true;
                case "plan":
                    if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) { error = "Plano sem steps."; return false; }
                    response = new("plan", null, new(steps.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()), null); return true;
                case "final":
                    var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    var files = root.TryGetProperty("files_changed", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
                    if (string.IsNullOrWhiteSpace(summary)) { error = "Final sem summary."; return false; }
                    response = new("final", null, null, new(summary, files)); return true;
                default: error = "Tipo de resposta inválido."; return false;
            }
        }
        catch (JsonException ex) { error = "JSON inválido: " + ex.Message; return false; }
    }
}

public sealed record ModelTurn(string Role, string Content);
public sealed record ModelRequest(string SystemPrompt, IReadOnlyList<ModelTurn> Turns);
public interface IModelAdapter { string ModelId { get; } Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct); }
