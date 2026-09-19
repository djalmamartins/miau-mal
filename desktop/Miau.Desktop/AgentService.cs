using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Miau.Desktop;

public sealed class AgentService
{
    readonly HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = Timeout.InfiniteTimeSpan };
    static readonly HashSet<string> Allowed = ["list_files", "read_file", "write_file", "search", "run_command", "git_status", "git_diff"];
    static readonly string[] Dangerous = ["git reset --hard", "git clean", "git push --force", "rm -rf", "rmdir /s", "del /f /s", "format ", "shutdown", "reboot"];

    public async Task<string> RunAsync(string root, string prompt, CancellationToken ct, Action<string> progress)
    {
        var messages = new List<ChatMessage>
        {
            new("system", SystemPrompt + $"\n\nPROJETO JÁ ABERTO PELO APLICATIVO:\nDiretório raiz: {root}\nVocê já está operando dentro desse diretório. Nunca peça ao usuário o caminho do projeto. Caminhos das ferramentas são relativos a essa raiz. Se a tarefa pede análise do projeto, comece inspecionando-o com as ferramentas disponíveis."),
            new("user", prompt)
        };

        string? lastSignature = null;
        var repeated = 0;

        for (var step = 0; step < 30; step++)
        {
            progress($"● Etapa {step + 1}: consultando qwen2.5-coder:7b…");
            using var r = await http.PostAsJsonAsync("/api/chat", new
            {
                model = "qwen2.5-coder:7b",
                messages,
                tools = ToolDefinitions,
                stream = false,
                options = new { temperature = .1 }
            }, ct);
            r.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var message = doc.RootElement.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            var calls = ParseNativeCalls(message);
            if (calls.Count == 0)
                calls.AddRange(ParseTextCalls(content));

            if (calls.Count == 0)
                return string.IsNullOrWhiteSpace(content) ? "O modelo encerrou sem produzir uma resposta." : content;

            var assistantCalls = new List<ToolCall>();
            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                var signature = call.Name + ":" + call.Args.GetRawText();
                repeated = signature == lastSignature ? repeated + 1 : 0;
                lastSignature = signature;
                if (repeated >= 2)
                {
                    messages.Add(new("user", $"A ferramenta {call.Name} foi solicitada repetidamente com os mesmos argumentos. Não a repita. Use o resultado já recebido e avance para a próxima etapa ou dê a resposta final."));
                    repeated = 0;
                    continue;
                }

                progress(Describe(call.Name, call.Args));
                string output;
                try { output = await Execute(root, call.Name, call.Args, ct); }
                catch (Exception ex) { output = "ERRO DA FERRAMENTA: " + ex.Message; }

                assistantCalls.Add(new ToolCall(new ToolFunction(call.Name, call.Args)));
                // Do not feed textual JSON calls back as normal assistant prose.
                // For native calls preserve tool_calls; for textual fallback give the result
                // as an explicit observation so Qwen can continue reliably.
                if (message.TryGetProperty("tool_calls", out _))
                {
                    messages.Add(new("assistant", content, assistantCalls.ToArray()));
                    messages.Add(new("tool", Trim(output), null, call.Name));
                }
                else
                {
                    messages.Add(new("assistant", $"Vou executar {call.Name}."));
                    messages.Add(new("user", $"RESULTADO DA FERRAMENTA {call.Name}:\n{Trim(output)}\nUse este resultado. Não repita esta ferramenta sem necessidade; continue a tarefa."));
                }
                assistantCalls.Clear();
            }
        }
        return "O agente atingiu o limite de 30 etapas. A tarefa foi interrompida para evitar um loop.";
    }

    static List<(string Name, JsonElement Args)> ParseNativeCalls(JsonElement message)
    {
        var result = new List<(string, JsonElement)>();
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return result;
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var fn) || !fn.TryGetProperty("name", out var n)) continue;
            var name = n.GetString();
            if (name is null || !Allowed.Contains(name)) continue;
            var args = fn.TryGetProperty("arguments", out var a) ? a.Clone() : EmptyArgs();
            result.Add((name, args));
        }
        return result;
    }

    static List<(string Name, JsonElement Args)> ParseTextCalls(string s)
    {
        var result = new List<(string, JsonElement)>();
        // Qwen may emit several JSON tool calls on separate lines instead of native tool_calls.
        foreach (Match m in Regex.Matches(s, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}"))
        {
            try
            {
                using var d = JsonDocument.Parse(m.Value);
                var x = d.RootElement;
                if (!x.TryGetProperty("name", out var n)) continue;
                var name = n.GetString();
                if (name is null || !Allowed.Contains(name)) continue;
                var args = x.TryGetProperty("arguments", out var a) ? a.Clone() : EmptyArgs();
                result.Add((name, args));
            }
            catch { }
        }
        return result;
    }

    static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement.Clone();

    static async Task<string> Execute(string root, string name, JsonElement a, CancellationToken ct)
    {
        string Arg(string n, string d = "")
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(n, out var x)) return d;
            return x.ValueKind == JsonValueKind.String ? x.GetString() ?? d : x.ToString();
        }
        string Safe(string p)
        {
            var basePath = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(root, p));
            var prefix = basePath.EndsWith(Path.DirectorySeparatorChar) ? basePath : basePath + Path.DirectorySeparatorChar;
            if (full != basePath && !full.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Caminho fora do projeto.");
            return full;
        }

        return name switch
        {
            "list_files" => ListFiles(Safe(Arg("path", "."))),
            "read_file" => await File.ReadAllTextAsync(Safe(Arg("path")), ct),
            "write_file" => await Write(Safe(Arg("path")), Arg("content"), ct),
            "search" => Search(root, Arg("query")),
            "git_status" => await Cmd(root, "git status --short --branch", ct),
            "git_diff" => await Cmd(root, "git diff", ct),
            "run_command" => await SafeCmd(root, Arg("command"), ct),
            _ => "Ferramenta desconhecida"
        };
    }

    static string ListFiles(string path)
    {
        if (!Directory.Exists(path)) return "Diretório não encontrado: " + path;
        return string.Join("\n", Directory.EnumerateFileSystemEntries(path)
            .Where(p => !Ignored(p))
            .Take(300)
            .Select(p => (Directory.Exists(p) ? "[dir] " : "[file] ") + Path.GetFileName(p)));
    }

    static string Search(string root, string query) =>
        string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !Ignored(p))
            .SelectMany(p => Find(p, query))
            .Take(200));

    static bool Ignored(string p)
    {
        var s = p.Replace('\\', '/');
        return s.Contains("/.git/") || s.Contains("/bin/") || s.Contains("/obj/") || s.Contains("/node_modules/");
    }

    static async Task<string> Write(string p, string c, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, c, ct);
        return $"Arquivo salvo: {p}";
    }

    static IEnumerable<string> Find(string p, string q)
    {
        IEnumerable<string> lines;
        try { lines = File.ReadLines(p); } catch { yield break; }
        var i = 0;
        foreach (var l in lines) { i++; if (l.Contains(q, StringComparison.OrdinalIgnoreCase)) yield return $"{p}:{i}: {l}"; }
    }

    public Task<string> GetGitStatusAsync(string root, CancellationToken ct) => Cmd(root, "git status --short", ct);
    public async Task<string> GetGitDiffAsync(string root, CancellationToken ct)
    {
        var tracked = await Cmd(root, "git diff --no-color", ct);
        var untracked = await Cmd(root, "git ls-files --others --exclude-standard", ct);
        if (string.IsNullOrWhiteSpace(untracked)) return tracked;
        var blocks = new List<string>();
        foreach (var rel in untracked.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!File.Exists(full)) continue;
            string body;
            try { body = await File.ReadAllTextAsync(full, ct); }
            catch { body = "[arquivo binário ou não textual]"; }
            blocks.Add($"--- /dev/null\n+++ b/{rel}\n@@ arquivo novo @@\n{body}");
        }
        return string.Join("\n", new[] { tracked, string.Join("\n\n", blocks) }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    static Task<string> SafeCmd(string root, string command, CancellationToken ct)
    {
        if (Dangerous.Any(x => command.Contains(x, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Comando bloqueado por segurança: " + command);
        return Cmd(root, command, ct);
    }

    static string Describe(string name, JsonElement a)
    {
        string Arg(string n)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(n, out var x)) return "";
            return x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString();
        }
        return name switch
        {
            "list_files" => $"▸ Listando arquivos: {Arg("path")}",
            "read_file" => $"▸ Lendo: {Arg("path")}",
            "write_file" => $"▸ Alterando: {Arg("path")}",
            "search" => $"▸ Pesquisando: {Arg("query")}",
            "run_command" => $"▸ Executando: {Arg("command")}",
            "git_status" => "▸ Verificando Git status",
            "git_diff" => "▸ Revisando alterações (git diff)",
            _ => $"▸ {name}"
        };
    }

    static async Task<string> Cmd(string root, string command, CancellationToken ct)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
            psi = new("cmd.exe", "/d /s /c \"" + command.Replace("\"", "\\\"") + "\"");
        else
            psi = new("/bin/zsh", "-lc \"" + command.Replace("\"", "\\\"") + "\"");
        psi.WorkingDirectory = root;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o comando.");
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (await o) + "\n" + (await e);
    }

    static string Trim(string s) => s.Length > 30000 ? s[..30000] + "\n[truncado]" : s;

    record ChatMessage(
        string role,
        string content,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolCall[]? tool_calls = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? tool_name = null);
    record ToolCall(ToolFunction function);
    record ToolFunction(string name, JsonElement arguments);

    static readonly object[] ToolDefinitions =
    [
        Tool("list_files", "Lista arquivos e diretórios de um caminho do projeto.", new { path = new { type = "string", description = "Caminho relativo. Use . para a raiz." } }),
        Tool("read_file", "Lê um arquivo de texto do projeto.", new { path = new { type = "string" } }, ["path"]),
        Tool("write_file", "Cria ou substitui um arquivo no projeto.", new { path = new { type = "string" }, content = new { type = "string" } }, ["path", "content"]),
        Tool("search", "Pesquisa texto nos arquivos do projeto.", new { query = new { type = "string" } }, ["query"]),
        Tool("run_command", "Executa um comando no terminal dentro do projeto.", new { command = new { type = "string" } }, ["command"]),
        Tool("git_status", "Executa git status no projeto.", new { }),
        Tool("git_diff", "Mostra o git diff do projeto.", new { })
    ];

    static object Tool(string name, string description, object properties, string[]? required = null) =>
        new { type = "function", function = new { name, description, parameters = new { type = "object", properties, required = required ?? [] } } };

    const string SystemPrompt = """
Você é MIAU, um desenvolvedor sênior local extremamente competente e levemente ranzinza. Trabalhe somente no projeto aberto.
Sua personalidade é seca, direta, inteligente e bem-humorada: você pode reclamar brevemente de código ruim, complexidade desnecessária, gambiarras, duplicação e bugs óbvios. Use ironia leve e ocasional, nunca hostilidade.
Não insulte o usuário, autores do código ou outras pessoas. Não seja arrogante, ofensivo ou cansativo. Nunca sacrifique clareza, precisão, segurança ou qualidade técnica pela piada. Em situações críticas, erros graves ou quando o usuário precisa de orientação, seja claro antes de ser engraçado.
Prefira comentários curtos como "Claro que era um null sem tratamento. Corrigindo." ou "Isso funciona. Não quer dizer que esteja bom." Não repita bordões e não force humor em toda mensagem.
Use as ferramentas fornecidas sempre que precisar inspecionar ou agir no projeto. Não escreva chamadas de ferramenta como texto/JSON quando puder usar tool_calls.
Depois de receber o resultado de uma ferramenta, use esse resultado e avance; não repita a mesma chamada sem necessidade.
Inspecione antes de editar, faça mudanças pequenas e rode testes quando apropriado.
Nunca faça commit, push, reset, clean ou exclusões sem pedido explícito.
Quando terminar, responda em português com um resumo objetivo e os testes executados.
""";
}
