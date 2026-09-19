using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Miau.Agent;

public sealed class OllamaAgent(HttpClient httpClient)
{
    public async Task<AgentResult> RunAsync(AgentRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.MaxSteps is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request.MaxSteps));

        var tools = new WorkspaceTools(request.Workspace);
        var events = new List<AgentEvent>();
        var messages = new JsonArray {
            new JsonObject { ["role"]="system", ["content"]=SystemPrompt },
            new JsonObject { ["role"]="user", ["content"]=request.Prompt }
        };

        for (var step = 1; step <= request.MaxSteps; step++)
        {
            var payload = new JsonObject {
                ["model"]=request.Model,
                ["stream"]=false,
                ["messages"]=messages.DeepClone(),
                ["tools"]=ToolDefinitions.DeepClone(),
                ["options"]=new JsonObject { ["temperature"]=0.1 }
            };

            using var response = await httpClient.PostAsJsonAsync("/api/chat", payload, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {raw}");

            using var doc = JsonDocument.Parse(raw);
            var message = doc.RootElement.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            var calls = ReadToolCalls(message, content);
            if (calls.Count == 0)
                return new(content, events);

            messages.Add(BuildAssistantMessage(content, calls));
            foreach (var call in calls)
            {
                events.Add(new("tool", $"{step}: {call.Name}"));
                string result;
                try { result = await ExecuteAsync(tools, call.Name, call.Arguments, ct); }
                catch (Exception ex) { result = $"tool_error: {ex.Message}"; }

                messages.Add(new JsonObject {
                    ["role"]="tool",
                    ["tool_name"]=call.Name,
                    ["content"]=Trim(result, 30000)
                });
            }
        }

        return new("Limite de passos atingido. Continue a tarefa em uma nova execução.", events);
    }

    private static List<ToolCall> ReadToolCalls(JsonElement message, string content)
    {
        var result = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var fn)) continue;
                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var args = fn.TryGetProperty("arguments", out var a) ? ParseArguments(a) : new JsonObject();
                result.Add(new(name, args));
            }
        }

        if (result.Count == 0 && TryParseTextToolCall(content, out var fallback))
            result.Add(fallback);

        return result;
    }

    private static JsonObject ParseArguments(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return JsonNode.Parse(value.GetRawText())?.AsObject() ?? new JsonObject();
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                try { return JsonNode.Parse(text)?.AsObject() ?? new JsonObject(); } catch (JsonException) { }
        }
        return new JsonObject();
    }

    private static bool TryParseTextToolCall(string content, out ToolCall call)
    {
        call = default!;
        if (string.IsNullOrWhiteSpace(content)) return false;
        var text = content.Trim();
        var fenced = Regex.Match(text, @"^```(?:json)?\s*(\{[\s\S]*\})\s*```$", RegexOptions.IgnoreCase);
        if (fenced.Success) text = fenced.Groups[1].Value;
        if (!text.StartsWith('{') || !text.EndsWith('}')) return false;

        try
        {
            var obj = JsonNode.Parse(text)?.AsObject();
            var name = obj?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || !KnownTools.Contains(name)) return false;
            var args = obj?["arguments"] as JsonObject ?? new JsonObject();
            call = new(name, (JsonObject)args.DeepClone());
            return true;
        }
        catch (Exception) { return false; }
    }

    private static JsonObject BuildAssistantMessage(string content, IReadOnlyList<ToolCall> calls)
    {
        var toolCalls = new JsonArray();
        foreach (var call in calls)
            toolCalls.Add(new JsonObject {
                ["function"]=new JsonObject {
                    ["name"]=call.Name,
                    ["arguments"]=call.Arguments.DeepClone()
                }
            });
        return new JsonObject { ["role"]="assistant", ["content"]=content, ["tool_calls"]=toolCalls };
    }

    private static async Task<string> ExecuteAsync(WorkspaceTools t, string name, JsonObject a, CancellationToken ct) => name switch {
        "list_files"=>t.ListFiles(Get(a,"path",".")),
        "read_file"=>await t.ReadFileAsync(Get(a,"path"),ct),
        "write_file"=>await t.WriteFileAsync(Get(a,"path"),Get(a,"content"),ct),
        "search"=>t.Search(Get(a,"query")),
        "run_command"=>await t.RunCommandAsync(Get(a,"command"),ct),
        "git_status"=>await t.GitStatusAsync(ct),
        "git_diff"=>await t.GitDiffAsync(ct),
        "git_log"=>await t.GitLogAsync(ct),
        _=>throw new InvalidOperationException($"Unknown tool: {name}")
    };

    private static string Get(JsonObject e, string n, string? fallback=null)
    {
        if (e.TryGetPropertyValue(n, out var value) && value is not null)
            return value.GetValue<string>();
        return fallback ?? throw new ArgumentException($"Missing argument: {n}");
    }

    private static string Trim(string v,int m)=>v.Length<=m?v:v[..m]+"\n...[truncated]";
    private sealed record ToolCall(string Name, JsonObject Arguments);
    private static readonly HashSet<string> KnownTools = ["list_files","read_file","write_file","search","run_command","git_status","git_diff","git_log"];

    private const string SystemPrompt = """
You are MIAU, a local autonomous coding agent. Inspect the repository before editing. Use the provided tools instead of printing tool-call JSON.
Use tools to read/search/create/edit files, find errors, run builds/tests/linters, and inspect/use Git. Stay inside the opened workspace.
Do not claim tests passed unless run. Do not commit, push, reset, clean, checkout destructive changes, or rewrite history unless the user explicitly asks.
When finished, give the user a concise Portuguese summary of findings, changed files, commands/tests and limitations.
""";

    private static readonly JsonArray ToolDefinitions = JsonNode.Parse("""
[
{"type":"function","function":{"name":"list_files","description":"List workspace files","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}},
{"type":"function","function":{"name":"read_file","description":"Read a text file","parameters":{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}}},
{"type":"function","function":{"name":"write_file","description":"Create or replace a text file","parameters":{"type":"object","required":["path","content"],"properties":{"path":{"type":"string"},"content":{"type":"string"}}}}},
{"type":"function","function":{"name":"search","description":"Search text in the workspace","parameters":{"type":"object","required":["query"],"properties":{"query":{"type":"string"}}}}},
{"type":"function","function":{"name":"run_command","description":"Run a command in the workspace","parameters":{"type":"object","required":["command"],"properties":{"command":{"type":"string"}}}}},
{"type":"function","function":{"name":"git_status","description":"Read Git status","parameters":{"type":"object","properties":{}}}},
{"type":"function","function":{"name":"git_diff","description":"Read Git diff","parameters":{"type":"object","properties":{}}}},
{"type":"function","function":{"name":"git_log","description":"Read recent Git history","parameters":{"type":"object","properties":{}}}}
]
""")!.AsArray();
}
