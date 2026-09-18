using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        for (var step=1; step<=request.MaxSteps; step++)
        {
            var payload = new JsonObject { ["model"]=request.Model, ["stream"]=false, ["messages"]=messages.DeepClone(), ["tools"]=ToolDefinitions.DeepClone() };
            using var response = await httpClient.PostAsJsonAsync("/api/chat", payload, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {raw}");
            using var doc = JsonDocument.Parse(raw);
            var message = doc.RootElement.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            messages.Add(JsonNode.Parse(message.GetRawText())!.DeepClone());
            if (!message.TryGetProperty("tool_calls", out var calls) || calls.GetArrayLength()==0) return new(content, events);
            foreach (var call in calls.EnumerateArray())
            {
                var fn=call.GetProperty("function"); var name=fn.GetProperty("name").GetString() ?? "";
                events.Add(new("tool",$"{step}: {name}"));
                var result=await ExecuteAsync(tools,name,fn.GetProperty("arguments"),ct);
                messages.Add(new JsonObject { ["role"]="tool", ["content"]=Trim(result,30000) });
            }
        }
        return new("Limite de passos atingido. Continue a tarefa em uma nova execução.", events);
    }

    private static async Task<string> ExecuteAsync(WorkspaceTools t,string name,JsonElement a,CancellationToken ct) => name switch {
        "list_files"=>t.ListFiles(Get(a,"path",".")), "read_file"=>await t.ReadFileAsync(Get(a,"path"),ct),
        "write_file"=>await t.WriteFileAsync(Get(a,"path"),Get(a,"content"),ct), "search"=>t.Search(Get(a,"query")),
        "run_command"=>await t.RunCommandAsync(Get(a,"command"),ct), "git_status"=>await t.GitStatusAsync(ct),
        "git_diff"=>await t.GitDiffAsync(ct), "git_log"=>await t.GitLogAsync(ct), _=>throw new InvalidOperationException($"Unknown tool: {name}") };
    private static string Get(JsonElement e,string n,string? f=null)=>e.TryGetProperty(n,out var v)?v.GetString()??f??"":f??throw new ArgumentException($"Missing argument: {n}");
    private static string Trim(string v,int m)=>v.Length<=m?v:v[..m]+"\n...[truncated]";

    private const string SystemPrompt = """
You are MIAU, a local autonomous coding agent. Inspect the repository before editing. Use tools to read/search/create/edit files,
find errors, run builds/tests/linters, and inspect/use Git. Stay inside the opened workspace. Do not claim tests passed unless run.
Do not commit, push, reset, clean, checkout destructive changes, or rewrite history unless the user explicitly asks.
When finished, summarize changed files, commands/tests and limitations in Portuguese.
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
