using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Miau.Desktop;

public sealed class AgentService
{
    readonly HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = Timeout.InfiniteTimeSpan };
    public string Model { get; set; } = "qwen2.5-coder:7b";
    static readonly HashSet<string> Allowed = ["list_files", "read_file", "write_file", "replace_in_file", "search", "run_command", "git_status", "git_diff"];
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
        var asksForProjectState = Regex.IsMatch(prompt, @"(estado\s+atual|status\s+do\s+projeto|em\s+que\s+ponto|pr[oó]ximos?\s+passos?|situa[cç][aã]o\s+atual)", RegexOptions.IgnoreCase);
        var asksForCodeChange = Regex.IsMatch(prompt, @"\b(fa[cç]a|altere|modifique|edite|implemente|adicione|corrija|crie|remova|refatore)\b", RegexOptions.IgnoreCase);
        var projectAnalysisRequested = asksForProjectState && !asksForCodeChange;
        var readOnlyRequested = Regex.IsMatch(prompt, @"(não|nao)\s+(altere|modifique|edite|mude)", RegexOptions.IgnoreCase);
        var inspectedRoot = false;
        var inspectedCentralFile = false;
        var analysisNudges = 0;
        var actionNudges = 0;
        if (projectAnalysisRequested)
        {
            progress("▸ Pré-inspeção determinística do projeto");
            var rootListing = ListFiles(Path.GetFullPath(root));
            inspectedRoot = true;
            messages.Add(new("user", $"INSPEÇÃO AUTOMÁTICA — RAIZ DO PROJETO:\n{Trim(rootListing)}"));

            // Prefer the currently running MIAU Desktop implementation over legacy/root planning docs.
            // Root documentation is still useful, but code on the active branch is stronger evidence of current state.
            var candidates = new List<string>();
            var desktopRoot = Path.Combine(root, "desktop", "Miau.Desktop");
            if (Directory.Exists(desktopRoot))
            {
                foreach (var name in new[] { "Miau.Desktop.csproj", "Program.cs", "App.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs", "AgentService.cs" })
                {
                    var active = Path.Combine(desktopRoot, name);
                    if (File.Exists(active)) candidates.Add(active);
                }
            }

            // For current-state analysis, do not preload legacy root README/roadmap or unrelated
            // projects. They can be fetched later if the model explicitly needs historical context.
            // The automatic snapshot must describe the application that is actually running now.

            messages.Add(new("user", "REGRA DE EVIDÊNCIA: determine o estado ATUAL principalmente pelo código existente na branch ativa. Documentos de roadmap/arquitetura podem estar históricos ou desatualizados. Se documentação e implementação divergirem, diga isso explicitamente e priorize o que o código atual comprova. Não afirme que testes foram executados apenas porque git_status/git_diff foram usados."));
            // Put the evidence rule after the inspected files too. Small local models overweight older/root
            // documents, so the final instruction must explicitly reconcile them with the active desktop code.
            var selectedAnalysisFiles = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            var snapshotBodies = new List<string>();
            foreach (var file in selectedAnalysisFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var body = await File.ReadAllTextAsync(file, ct);
                    snapshotBodies.Add($"--- {Path.GetRelativePath(root, file)} ---\n{Trim(body)}");
                    inspectedCentralFile = true;
                    progress($"▸ Lendo automaticamente: {Path.GetRelativePath(root, file)}");
                }
                catch { }
            }

            var inspectedNames = string.Join(", ", selectedAnalysisFiles.Select(p => Path.GetRelativePath(root, p)));
            var desktopFacts = new List<string>();
            if (Directory.Exists(desktopRoot))
            {
                desktopFacts.Add("Aplicação desktop ativa: desktop/Miau.Desktop");
                desktopFacts.Add("Stack comprovada pelo projeto atual: Avalonia + .NET 10");
                if (File.Exists(Path.Combine(desktopRoot, "AgentService.cs")))
                    desktopFacts.Add("AgentService existe: o desktop já possui orquestração de agente e ferramentas locais.");
                if (File.Exists(Path.Combine(desktopRoot, "MemoryService.cs")))
                    desktopFacts.Add("MemoryService existe: já há memória local por projeto.");
                if (File.Exists(Path.Combine(desktopRoot, "ConversationService.cs")))
                    desktopFacts.Add("ConversationService existe: já há persistência local de conversas.");
                if (File.Exists(Path.Combine(desktopRoot, "GitHubJobService.cs")))
                    desktopFacts.Add("GitHubJobService existe: já há base para trabalho autônomo via GitHub.");
                if (File.Exists(Path.Combine(desktopRoot, "TaskReportService.cs")))
                    desktopFacts.Add("TaskReportService existe: já há geração de relatórios de tarefas.");
            }
            desktopFacts.Add($"Modelo local configurado nesta execução: {Model}");
            desktopFacts.Add("O AgentService conversa com Ollama pela API local /api/chat; inferência local já está integrada via Ollama.");
            desktopFacts.Add("WinUI 3 e llama.cpp/GGUF pertencem a planejamento histórico e NÃO são o estado/rumo ativo do desktop atual.");
            desktopFacts.Add("git_status e git_diff são inspeções Git, NÃO testes executados.");
            // Replace the original conversation for project-state analysis. Keeping the generic system
            // prompt plus many historical/tool messages lets small models anchor on stale plans.
            messages.Clear();
            messages.Add(new("system", SystemPrompt + $"\n\nVocê está analisando SOMENTE o estado atual do aplicativo MIAU Desktop em {root}. O snapshot abaixo foi produzido pelo próprio agente a partir do código da branch ativa e é a fonte autoritativa. Ignore planos históricos conflitantes. Não cite WinUI 3, llama.cpp/GGUF ou reconstrução da CLI como próximos passos. git_status/git_diff não são testes."));
            messages.Add(new("user", prompt + "\n\nSNAPSHOT ESTRUTURADO — FATOS ATUAIS:\n- " + string.Join("\n- ", desktopFacts) + $"\nArquivos lidos: {inspectedNames}\n\nCÓDIGO ATUAL INSPECIONADO:\n" + string.Join("\n\n", snapshotBodies) + "\n\nResponda com: estado atual comprovado, 3 próximos passos derivados do desktop atual e observações técnicas. Não invente testes executados."));
            inspectedRoot = true;
            inspectedCentralFile = snapshotBodies.Count > 0;
        }

        if (projectAnalysisRequested)
        {
            // Project-state reports must be factual and deterministic. Small local models can anchor on
            // stale training/repository plans even when given contradictory current code.
            var current = new List<string>
            {
                "MIAU Desktop está implementado em Avalonia sobre .NET 10.",
                $"A inferência local está integrada via Ollama; modelo configurado: {Model}.",
                "AgentService já orquestra o modelo e ferramentas locais de projeto/Git.",
                File.Exists(Path.Combine(root, "desktop", "Miau.Desktop", "MemoryService.cs")) ? "MemoryService já fornece memória local por projeto." : "",
                File.Exists(Path.Combine(root, "desktop", "Miau.Desktop", "ConversationService.cs")) ? "ConversationService já persiste conversas localmente." : "",
                File.Exists(Path.Combine(root, "desktop", "Miau.Desktop", "GitHubJobService.cs")) ? "GitHubJobService já contém a base do modo autônomo via GitHub." : "",
                File.Exists(Path.Combine(root, "desktop", "Miau.Desktop", "TaskReportService.cs")) ? "TaskReportService já gera relatórios de tarefas." : ""
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            var next = new[]
            {
                "Validar e endurecer o ciclo do agente (ferramentas, timeout, cancelamento, erros e prevenção de loops).",
                "Validar o modo autônomo ponta a ponta com GitHub, incluindo reserva de tarefa, branch, testes, commit, push e PR sem merge automático.",
                "Concluir a experiência desktop: estados de execução/travamento, anexos/memória/histórico e empacotamento macOS/Windows."
            };

            return "**Estado atual comprovado**\n\n- " + string.Join("\n- ", current) +
                   "\n\n**3 próximos passos**\n\n1. " + next[0] + "\n2. " + next[1] + "\n3. " + next[2] +
                   "\n\n**Observação técnica**\n\nEste diagnóstico foi gerado a partir do snapshot do código atual. Planos históricos como WinUI 3 ou llama.cpp/GGUF não são tratados como direção ativa sem evidência no código atual. Nenhum teste foi executado por este diagnóstico.";
        }

        for (var step = 0; step < 30; step++)
        {
            progress($"● Etapa {step + 1}: consultando {Model}…");
            using var r = await http.PostAsJsonAsync("/api/chat", new
            {
                model = Model,
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
            {
                if (asksForCodeChange && Regex.IsMatch(content, @"\b(vou|irei|vamos)\b.{0,80}\b(execut|usar|verific|analis|ler|abrir|alter|modific|edit)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    actionNudges++;
                    if (actionNudges > 3)
                        return "A tarefa exigia uma alteração, mas o modelo ficou descrevendo a próxima ação sem executá-la. Interrompi para evitar fingir progresso.";
                    messages.Add(new("assistant", content));
                    messages.Add(new("user", "Não descreva a próxima ação. Execute-a agora por tool_call. Continue usando ferramentas até a alteração solicitada estar realmente aplicada e verificada; só então responda ao usuário."));
                    continue;
                }

                if (projectAnalysisRequested && (!inspectedRoot || !inspectedCentralFile))
                {
                    analysisNudges++;
                    if (analysisNudges > 2)
                        return "Não consegui concluir a inspeção mínima do projeto com segurança. O modelo local não executou as leituras exigidas; interrompi em vez de fingir uma análise.";
                    messages.Add(new("assistant", content));
                    messages.Add(new("user", "A análise ainda está incompleta. Execute AGORA as ferramentas necessárias: list_files na raiz e read_file em README.md e/ou no arquivo .csproj/solution/entry point que aparecer. Não responda com intenção, promessa ou comentário; faça as chamadas de ferramenta."));
                    continue;
                }
                return string.IsNullOrWhiteSpace(content) ? "O modelo encerrou sem produzir uma resposta." : content;
            }

            var assistantCalls = new List<ToolCall>();
            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                var signature = call.Name + ":" + call.Args.GetRawText();
                repeated = signature == lastSignature ? repeated + 1 : 0;
                lastSignature = signature;
                if (repeated >= 2)
                {
                    messages.Add(new("user", $"A ferramenta {call.Name} foi solicitada repetidamente com os mesmos argumentos. Não a repita. Use o resultado já recebido e avance para outra ferramenta necessária ou dê a resposta final."));
                    repeated = 0;
                    continue;
                }

                if (readOnlyRequested && call.Name is "write_file" or "replace_in_file")
                {
                    messages.Add(new("user", $"A tarefa é somente leitura. A ferramenta {call.Name} está bloqueada. Use apenas list_files, read_file, search, git_status ou git_diff."));
                    continue;
                }
                progress(Describe(call.Name, call.Args));
                string output;
                try
                {
                    output = await Execute(root, call.Name, call.Args, ct);
                    if (call.Name == "list_files") inspectedRoot = true;
                    if (call.Name == "read_file")
                    {
                        var raw = call.Args.ValueKind == JsonValueKind.Object && call.Args.TryGetProperty("path", out var rp) ? rp.GetString() ?? "" : "";
                        var file = Path.GetFileName(raw);
                        if (file.Equals("README.md", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || file is "Program.cs" or "App.axaml.cs" or "MainWindow.axaml.cs") inspectedCentralFile = true;
                    }
                }
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
            "replace_in_file" => await Replace(Safe(Arg("path")), Arg("old_text"), Arg("new_text"), ct),
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

    static async Task<string> Replace(string p, string oldText, string newText, CancellationToken ct)
    {
        var current = await File.ReadAllTextAsync(p, ct);
        var count = Regex.Matches(current, Regex.Escape(oldText)).Count;
        if (count != 1) throw new InvalidOperationException($"Edição recusada: esperado 1 trecho correspondente, encontrado {count}.");
        await File.WriteAllTextAsync(p, current.Replace(oldText, newText), ct);
        return $"Trecho alterado: {p}";
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
            "write_file" => $"▸ Gravando arquivo: {Arg("path")}",
            "replace_in_file" => $"▸ Editando trecho: {Arg("path")}",
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
        var stdout = await o;
        var stderr = await e;
        if (p.ExitCode != 0) throw new InvalidOperationException($"Comando falhou ({p.ExitCode}): {stderr.Trim()}");
        return stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr);
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
        Tool("write_file", "Cria ou substitui um arquivo no projeto. Prefira replace_in_file para alterações localizadas.", new { path = new { type = "string" }, content = new { type = "string" } }, ["path", "content"]),
        Tool("replace_in_file", "Substitui exatamente uma ocorrência de um trecho em arquivo existente; ideal para edições pequenas e seguras.", new { path = new { type = "string" }, old_text = new { type = "string" }, new_text = new { type = "string" } }, ["path", "old_text", "new_text"]),
        Tool("search", "Pesquisa texto nos arquivos do projeto.", new { query = new { type = "string" } }, ["query"]),
        Tool("run_command", "Executa um comando no terminal dentro do projeto.", new { command = new { type = "string" } }, ["command"]),
        Tool("git_status", "Executa git status no projeto.", new { }),
        Tool("git_diff", "Mostra o git diff do projeto.", new { })
    ];

    static object Tool(string name, string description, object properties, string[]? required = null) =>
        new { type = "function", function = new { name, description, parameters = new { type = "object", properties, required = required ?? [] } } };

    const string SystemPrompt = """
Você é MIAU, um desenvolvedor sênior local extremamente competente e levemente ranzinza. Trabalhe somente no projeto aberto.
Sua personalidade é bem ranzinza, seca, direta, inteligente e engraçada. Reclame de código ruim, complexidade desnecessária, gambiarras, duplicação, dependências inúteis, nomes ruins e bugs óbvios. Use sarcasmo e ironia com frequência, especialmente ao encontrar problemas técnicos.
Faça pelo menos uma observação ranzinza curta na resposta final sempre que houver algo criticável no projeto. Pode demonstrar impaciência teatral com o código, nunca com o usuário.
Não insulte o usuário, autores do código ou outras pessoas. Não seja ofensivo. Nunca sacrifique clareza, precisão, segurança ou qualidade técnica pela piada. Primeiro entregue a informação útil; a rabugice acompanha a explicação.
Exemplos de tom (APENAS como referência de estilo; nunca copie literalmente): "Funciona. Milagre não é arquitetura.", "Achei a gambiarra. Ela estava confortável e já tinha endereço fixo.", "Build passou. Contra todas as expectativas do código.", "Isso aqui tem três abstrações para fazer o trabalho de um if. Impressionante."
A personalidade nunca substitui trabalho. Não envie mensagens intermediárias dizendo que vai continuar, que encontrou arquivos ou que pretende executar a tarefa. Se a solicitação pede uma ação, continue usando ferramentas até concluir, falhar de forma concreta ou precisar de uma decisão do usuário. Só então produza a resposta final.
Evite repetir bordões: varie naturalmente as reclamações e mantenha as piadas curtas.
Use as ferramentas fornecidas sempre que precisar inspecionar ou agir no projeto. Não escreva chamadas de ferramenta como texto/JSON quando puder usar tool_calls.
Quando o usuário pedir para analisar, revisar, entender, auditar ou dizer o estado do projeto, NÃO responda depois de apenas git_status/git_diff. Antes da resposta final, inspecione de verdade o projeto: liste a raiz, identifique arquivos de solução/projeto/documentação, leia pelo menos os arquivos centrais relevantes (por exemplo README, solution/project e entry points) e só então consulte Git. Adapte a profundidade ao pedido, mas nunca finja que status do Git é análise de projeto.
Se o pedido proibir alterações, use somente ferramentas de leitura/consulta e nunca write_file ou comandos que modifiquem arquivos.
Depois de receber o resultado de uma ferramenta, use esse resultado e avance; não repita a mesma chamada sem necessidade.
Inspecione antes de editar, faça mudanças pequenas e rode testes quando apropriado.
Nunca faça commit, push, reset, clean ou exclusões sem pedido explícito.
Quando terminar, responda em português com um resumo objetivo e os testes executados.
""";
}
