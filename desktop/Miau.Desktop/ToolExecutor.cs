using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miau.Desktop;

public sealed record ToolResult(string Tool, bool Success, string Output, string? Error, IReadOnlyDictionary<string, string> Metadata)
{
    public static ToolResult Ok(string tool, string output, params (string Key, string Value)[] metadata) =>
        new(tool, true, output, null, metadata.ToDictionary(x => x.Key, x => x.Value));
    public static ToolResult Fail(string tool, string error) => new(tool, false, "", error, new Dictionary<string, string>());
}

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(string workspace, MiauAction action, bool readOnly, CancellationToken ct);
    Task<ToolResult> ValidateAsync(string workspace, CancellationToken ct);
}

public sealed class ToolExecutor : IToolExecutor
{
    readonly IWebReferenceProvider references;
    public ToolExecutor(IWebReferenceProvider? references = null) => this.references = references ?? new HttpReferenceProvider();
    static readonly string[] Dangerous = ["git reset --hard", "git clean", "git push --force", "git push -f", "rm -rf", "rmdir /s", "del /f /s", "format ", "shutdown", "reboot"];
    public async Task<ToolResult> ExecuteAsync(string workspace, MiauAction action, bool readOnly, CancellationToken ct)
    {
        try
        {
            if (readOnly && action.Action is ToolNames.WriteFile or ToolNames.ReplaceInFile or ToolNames.DeleteFile or ToolNames.ApplyPatch or ToolNames.RunCommand or ToolNames.Build or ToolNames.Test)
                throw new InvalidOperationException("A tarefa é somente leitura; ferramentas mutáveis estão bloqueadas.");
            string Arg(string name, string fallback = "") => action.Arguments.TryGetValue(name, out var value) ? value : fallback;
            return action.Action switch
            {
                ToolNames.ListFiles => ToolResult.Ok(action.Action, ListFiles(SafePath(workspace, Arg("path", "."))), ("inspected_path", Arg("path", "."))),
                ToolNames.ReadFile => ToolResult.Ok(action.Action, await File.ReadAllTextAsync(SafePath(workspace, Arg("path")), ct), ("inspected_path", Arg("path"))),
                ToolNames.Search => ToolResult.Ok(action.Action, Search(workspace, Arg("query")), ("inspected_path", ".")),
                ToolNames.FetchUrl => await FetchUrl(Arg("url"), ct),
                ToolNames.RenderPage => await RenderPage(workspace, Arg("path", "index.html"), ParseViewport(Arg("width"), 1440), ParseViewport(Arg("height"), 1200), ct),
                ToolNames.InspectVisual => await InspectVisual(Arg("screenshot_path"), Arg("model", "llava:7b"), Arg("viewport", "desconhecido"), Arg("criteria"), Arg("structure"), ct),
                ToolNames.WriteFile => await Write(action.Action, SafePath(workspace, Arg("path")), Arg("content"), Arg("path"), ct),
                ToolNames.ReplaceInFile => await Replace(action.Action, SafePath(workspace, Arg("path")), Arg("old_text"), Arg("new_text"), Arg("path"), ct),
                ToolNames.DeleteFile => Delete(action.Action, SafePath(workspace, Arg("path")), Arg("path")),
                ToolNames.ApplyPatch => await ApplyPatch(action.Action, workspace, Arg("patch"), ct),
                ToolNames.GitStatus => ToolResult.Ok(action.Action, await Run(workspace, "git", ["status", "--short", "--branch"], ct)),
                ToolNames.GitDiff => await GitDiff(workspace, ct),
                ToolNames.Build => await Validation(action.Action, workspace, Arg("command", "dotnet build"), ct),
                ToolNames.Test => await Validation(action.Action, workspace, Arg("command", "dotnet test"), ct),
                ToolNames.RunCommand => await Command(action.Action, workspace, Arg("command"), ct),
                _ => ToolResult.Fail(action.Action, "Ferramenta desconhecida.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolResult.Fail(action.Action, ex.Message); }
    }

    static int ParseViewport(string value, int fallback) => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 240, 3840) : fallback;
    static async Task<ToolResult> InspectVisual(string screenshot, string model, string viewport, string criteria, string structure, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(screenshot) || !File.Exists(screenshot))
            return ToolResult.Fail(ToolNames.InspectVisual, "Screenshot não encontrado. Execute render_page primeiro e use o screenshot_path retornado.");
        var bytes = await File.ReadAllBytesAsync(screenshot, ct);
        var prompt = """
Você é o inspetor visual aterrado do MIAU. Analise somente o screenshot e as evidências fornecidas.
NÃO INVENTE ELEMENTOS. Se pedir revisão, liste ao menos um problema concreto no formato PROBLEMA, EVIDÊNCIA e PRIORIDADE. Comentários genéricos não justificam revisão.
Retorne um relatório curto e objetivo em português com:
1. hierarquia visual;
2. espaçamento/alinhamento;
3. tipografia/contraste;
4. responsividade aparente;
5. problemas visuais concretos;
6. melhorias prioritárias.
Cada problema deve usar exatamente: PROBLEMA, EVIDÊNCIA, LOCAL, PRIORIDADE e AÇÃO_SUGERIDA.
Não invente elementos que não aparecem na imagem. Termine com VEREDITO: APROVADO ou VEREDITO: REVISAR.
""";
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var payload = JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            messages = new[] { new { role = "user", content = $"{prompt}\nVIEWPORT: {viewport}\nCRITÉRIOS: {criteria}\nESTRUTURA DETECTADA: {structure}", images = new[] { Convert.ToBase64String(bytes) } } }
        });
        try
        {
            using var response = await client.PostAsync("http://127.0.0.1:11434/api/chat", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var hint = body.Contains("not found", StringComparison.OrdinalIgnoreCase) ? $" Modelo visual '{model}' não está instalado no Ollama." : "";
                return ToolResult.Fail(ToolNames.InspectVisual, $"Ollama visual respondeu HTTP {(int)response.StatusCode}.{hint}");
            }
            using var json = JsonDocument.Parse(body);
            var report = json.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(report)) return ToolResult.Fail(ToolNames.InspectVisual, "Modelo visual retornou relatório vazio.");
            var normalizedVerdict = NormalizeVisualVerdict(report);
            var actionable = HasActionableVisualProblem(report);
            var verdict = normalizedVerdict == "approved" || !actionable ? "approved" : "review";
            var highPriorityOpen = verdict == "review" && Regex.IsMatch(report, @"PRIORIDADE\s*:\s*(alta|high|crítica|critica)", RegexOptions.IgnoreCase);
            return ToolResult.Ok(ToolNames.InspectVisual, report,
                ("visual_inspection", "true"), ("visual_verdict", verdict), ("visual_actionable", actionable.ToString().ToLowerInvariant()), ("visual_high_priority_open", highPriorityOpen.ToString().ToLowerInvariant()), ("vision_model", model), ("screenshot_path", screenshot));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ToolResult.Fail(ToolNames.InspectVisual, "Tempo limite de 90s na inspeção visual."); }
        catch (HttpRequestException ex)
        { return ToolResult.Fail(ToolNames.InspectVisual, "Falha ao acessar o Ollama visual: " + ex.Message); }
    }

    internal static string NormalizeVisualVerdict(string report)
    {
        // Small local vision models do not always obey the exact requested token.
        // Accept only an explicit verdict near the end of the report, while
        // tolerating Portuguese inflections such as "Veredito: Aprovar".
        var tail = report.Length > 800 ? report[^800..] : report;
        var matches = System.Text.RegularExpressions.Regex.Matches(
            tail,
            @"veredito\s*:\s*(aprovad[oa]|aprovar|revisar)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (matches.Count == 0) return "review";
        var value = matches[^1].Groups[1].Value;
        return value.StartsWith("aprov", StringComparison.OrdinalIgnoreCase) ? "approved" : "review";
    }

    public static bool HasActionableVisualProblem(string report) => report.Contains("PROBLEMA", StringComparison.OrdinalIgnoreCase) && report.Contains("EVIDÊNCIA", StringComparison.OrdinalIgnoreCase) && report.Contains("LOCAL", StringComparison.OrdinalIgnoreCase) && report.Contains("PRIORIDADE", StringComparison.OrdinalIgnoreCase) && (report.Contains("AÇÃO_SUGERIDA", StringComparison.OrdinalIgnoreCase) || report.Contains("SUGGESTED_ACTION", StringComparison.OrdinalIgnoreCase));

    static async Task<ToolResult> RenderPage(string workspace, string relative, int width, int height, CancellationToken ct)
    {
        var page = SafePath(workspace, relative);
        if (!File.Exists(page)) return ToolResult.Fail(ToolNames.RenderPage, "Página não encontrada: " + relative);
        var outputDir = Path.Combine(Path.GetTempPath(), "miau-visual");
        Directory.CreateDirectory(outputDir);
        var screenshot = Path.Combine(outputDir, $"render-{Guid.NewGuid():N}.png");
        var browser = OperatingSystem.IsMacOS() ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" :
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe") : "google-chrome";
        if (Path.IsPathRooted(browser) && !File.Exists(browser))
            return ToolResult.Fail(ToolNames.RenderPage, "Google Chrome não encontrado para renderização visual.");
        var psi = new ProcessStartInfo(browser)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        psi.ArgumentList.Add("--headless=new");
        psi.ArgumentList.Add("--disable-gpu");
        psi.ArgumentList.Add("--hide-scrollbars");
        psi.ArgumentList.Add($"--window-size={width},{height}");
        psi.ArgumentList.Add("--screenshot=" + screenshot);
        psi.ArgumentList.Add(new Uri(page).AbsoluteUri);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o navegador.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || !File.Exists(screenshot))
            return ToolResult.Fail(ToolNames.RenderPage, "Chrome não conseguiu renderizar a página.");
        var info = new FileInfo(screenshot);
        return ToolResult.Ok(ToolNames.RenderPage,
            $"Página renderizada em {width}x{height}. Screenshot: {screenshot} ({info.Length} bytes).",
            ("visual_validation", "true"), ("screenshot_path", screenshot), ("screenshot_hash", ProgressTracker.ArtifactHash(await File.ReadAllBytesAsync(screenshot, ct))), ("viewport", $"{width}x{height}"), ("rendered_path", relative));
    }

    async Task<ToolResult> FetchUrl(string value, CancellationToken ct)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ToolResult.Fail(ToolNames.FetchUrl, "URL pública http/https inválida.");
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsPrivateAddress(uri.Host))
            return ToolResult.Fail(ToolNames.FetchUrl, "Acesso web local/loopback é bloqueado.");
        var result = await references.FetchAsync(uri, ct);
        if (!result.Available)
            return ToolResult.Ok(ToolNames.FetchUrl, $"Referência indisponível ({result.Reason}). O conteúdo NÃO foi analisado; continue somente com os requisitos do usuário e o projeto local.", ("url", uri.ToString()), ("reference_status", "unavailable"), ("reference_analyzed", "false"));
        var text = result.Content!; if (text.Length > 50000) text = text[..50000] + "\n[conteúdo web truncado]";
        return ToolResult.Ok(ToolNames.FetchUrl, text, ("url", uri.ToString()), ("content_type", result.ContentType ?? ""), ("reference_status", "available"), ("reference_analyzed", "true"));
    }

    static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    public async Task<ToolResult> ValidateAsync(string workspace, CancellationToken ct)
    {
        var candidates = await ValidationCandidates(workspace, ct);
        if (candidates.Any(x => x.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
            return await Validation(ToolNames.Test, workspace, "python3 -m compileall -q .", ct);

        var package = ClosestManifest(workspace, candidates, "package.json");
        if (package is not null)
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(package, ct));
            var scripts = json.RootElement.TryGetProperty("scripts", out var value) ? value : default;
            var commands = new List<string>();
            if (scripts.ValueKind == JsonValueKind.Object && scripts.TryGetProperty("build", out _)) commands.Add("npm run build");
            if (scripts.ValueKind == JsonValueKind.Object && scripts.TryGetProperty("test", out var test) && !test.ToString().Contains("no test specified", StringComparison.OrdinalIgnoreCase)) commands.Add("npm test -- --run");
            if (commands.Count == 0) return ToolResult.Ok(ToolNames.Test, "package.json sem scripts build/test; validação contextual não aplicável.", ("validation", "true"), ("project_type", "node"));
            var result = await Validation(ToolNames.Test, Path.GetDirectoryName(package)!, string.Join(" && ", commands), ct);
            return WithMetadata(result, "project_type", "node");
        }

        if (candidates.Any(x => new[] { ".html", ".css", ".js" }.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase)))
            return ValidateStatic(workspace, candidates);

        var project = ClosestProject(workspace, candidates);
        if (project is not null)
        {
            var relative = Path.GetRelativePath(workspace, project).Replace("\"", "\\\"");
            var result = await Validation(ToolNames.Build, workspace, $"dotnet build \"{relative}\"", ct);
            return WithMetadata(result, "project_type", "dotnet");
        }
        return ToolResult.Ok(ToolNames.Test, "Nenhum build/teste automático reconhecido; validação não aplicável.", ("validation", "true"));
    }

    static async Task<string[]> ValidationCandidates(string workspace, CancellationToken ct)
    {
        try
        {
            var output = await Run(workspace, "git", ["status", "--porcelain"], ct);
            var changed = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Length > 3 ? x[3..].Trim() : "").Where(x => x.Length > 0).ToArray();
            if (changed.Length > 0) return changed;
        }
        catch { }
        return Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Where(x => !Ignored(x)).Select(x => Path.GetRelativePath(workspace, x)).Take(2000).ToArray();
    }
    static string? ClosestManifest(string workspace, string[] candidates, string name) => candidates.Select(x => Path.GetDirectoryName(Path.Combine(workspace, x))).Where(x => x is not null).SelectMany(Ancestors).Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists);
    static IEnumerable<string> Ancestors(string? path) { while (!string.IsNullOrWhiteSpace(path)) { yield return path; path = Path.GetDirectoryName(path); } }
    static string? ClosestProject(string workspace, string[] candidates) => Directory.EnumerateFiles(workspace, "*.sln*", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? candidates.Select(x => Path.GetDirectoryName(Path.Combine(workspace, x))).Where(x => x is not null).SelectMany(Ancestors).SelectMany(x => Directory.EnumerateFiles(x, "*.csproj", SearchOption.TopDirectoryOnly)).FirstOrDefault();
    static ToolResult ValidateStatic(string workspace, string[] candidates)
    {
        var html = candidates.Where(x => x.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var relative in html)
        {
            var full = Path.Combine(workspace, relative); if (!File.Exists(full)) return ToolResult.Fail(ToolNames.Test, $"Arquivo estático ausente: {relative}");
            var text = File.ReadAllText(full); if (!text.Contains("<html", StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail(ToolNames.Test, $"Estrutura HTML mínima ausente: {relative}");
            foreach (Match match in Regex.Matches(text, "(?:href|src)=[\\\"']([^\\\"'#?]+)", RegexOptions.IgnoreCase))
            {
                var reference = match.Groups[1].Value; if (reference.Contains("://") || reference.StartsWith("data:")) continue;
                if (!File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, reference)))) return ToolResult.Fail(ToolNames.Test, $"Referência local inválida em {relative}: {reference}");
            }
        }
        return ToolResult.Ok(ToolNames.Test, $"Validação estática concluída: {html.Length} HTML; referências locais válidas.", ("validation", "true"), ("project_type", "static"));
    }
    static ToolResult WithMetadata(ToolResult result, string key, string value) => result with { Metadata = result.Metadata.Concat(new[] { new KeyValuePair<string, string>(key, value) }).ToDictionary(x => x.Key, x => x.Value) };

    static async Task<ToolResult> Write(string tool, string path, string content, string relative, CancellationToken ct)
    {
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        var oldHash = ContentHash(current ?? ""); var proposedHash = ContentHash(content);
        if (current == content)
            return ToolResult.Ok(tool, "NoEffectiveChange: a gravação proposta é idêntica ao conteúdo atual.", ("effective_change", "false"), ("target_path", relative), ("old_hash", oldHash), ("proposed_hash", proposedHash));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, content, ct);
        return ToolResult.Ok(tool, $"Arquivo salvo: {relative}", ("changed_path", relative), ("effective_change", "true"), ("target_path", relative), ("old_hash", oldHash), ("proposed_hash", proposedHash));
    }
    static ToolResult Delete(string tool, string path, string relative)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Arquivo não encontrado: {relative}");
        File.Delete(path);
        return ToolResult.Ok(tool, $"Arquivo removido: {relative}", ("changed_path", relative));
    }

    static async Task<ToolResult> Replace(string tool, string path, string oldText, string newText, string relative, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(oldText))
            throw new InvalidOperationException("Edição recusada: old_text não pode ser vazio.");

        var current = await File.ReadAllTextAsync(path, ct);
        var matches = CountOccurrences(current, oldText);

        if (matches == 0)
            throw new InvalidOperationException("Edição recusada: old_text não foi encontrado. Leia novamente o arquivo antes de editar ou use write_file para substituir o arquivo completo.");
        if (matches > 1)
            throw new InvalidOperationException($"Edição recusada: old_text corresponde a {matches} trechos. Envie um trecho maior e único, ou use write_file para substituir o arquivo completo.");

        var first = current.IndexOf(oldText, StringComparison.Ordinal); var proposed = current[..first] + newText + current[(first + oldText.Length)..];
        var oldHash = ContentHash(current); var proposedHash = ContentHash(proposed);
        if (proposed == current) return ToolResult.Ok(tool, "NoEffectiveChange: a substituição não altera o arquivo.", ("effective_change", "false"), ("target_path", relative), ("old_hash", oldHash), ("proposed_hash", proposedHash));
        await File.WriteAllTextAsync(path, proposed, ct);
        return ToolResult.Ok(tool, $"Trecho alterado: {relative}", ("changed_path", relative), ("effective_change", "true"), ("target_path", relative), ("old_hash", oldHash), ("proposed_hash", proposedHash));
    }

    static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }
    static async Task<ToolResult> ApplyPatch(string tool, string workspace, string patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch)) throw new InvalidOperationException("Patch vazio.");
        var temp = Path.Combine(Path.GetTempPath(), $"miau-{Guid.NewGuid():N}.patch");
        var baseline = WorkspaceBaseline.Capture(workspace);
        try { await File.WriteAllTextAsync(temp, patch, ct); var output = await Run(workspace, "git", ["apply", "--whitespace=nowarn", temp], ct); return PatchResult(tool, output, baseline.ChangesProducedNow(workspace)); }
        finally { File.Delete(temp); }
    }
    public static ToolResult PatchResult(string tool, string output, IReadOnlyCollection<string> delta) =>
        delta.Count == 0 ? ToolResult.Ok(tool, "NoEffectiveChange: o patch não alterou o workspace.", ("effective_change", "false")) : ToolResult.Ok(tool, output, ("changed_path", "(patch)"), ("effective_change", "true"));
    static string ContentHash(string value) => ProgressTracker.ArtifactHash(System.Text.Encoding.UTF8.GetBytes(value));
    static async Task<ToolResult> GitDiff(string workspace, CancellationToken ct)
    {
        var diff = await Run(workspace, "git", ["diff", "--no-color"], ct);
        var untracked = await Run(workspace, "git", ["ls-files", "--others", "--exclude-standard"], ct);
        var output = string.Join("\n", new[] { diff, untracked }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return ToolResult.Ok(ToolNames.GitDiff, output, ("has_changes", (!string.IsNullOrWhiteSpace(output)).ToString().ToLowerInvariant()));
    }
    static async Task<ToolResult> Validation(string tool, string workspace, string command, CancellationToken ct)
    {
        var result = await Command(tool, workspace, command, ct);
        return result.Success ? ToolResult.Ok(tool, result.Output, ("validation", "true")) : result;
    }
    static async Task<ToolResult> Command(string tool, string workspace, string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Fail(tool, "Comando vazio.");
        if (Dangerous.Any(x => command.Contains(x, StringComparison.OrdinalIgnoreCase))) return ToolResult.Fail(tool, "Comando destrutivo bloqueado: " + command);
        try
        {
            var (exe, args) = OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/d", "/s", "/c", command }) : ("/bin/zsh", new[] { "-lc", command });
            return ToolResult.Ok(tool, await Run(workspace, exe, args, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolResult.Fail(tool, ex.Message); }
    }
    static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Workspace obrigatório.");
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("Caminho obrigatório.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(basePath, relative)));

        // The workspace root itself is valid. This explicitly covers "." and equivalent paths.
        if (string.Equals(full, basePath, comparison)) return full;

        // Relative-path containment avoids false negatives caused by trailing separators while
        // still rejecting ../ escapes and rooted paths that resolve outside the workspace.
        var fromWorkspace = Path.GetRelativePath(basePath, full);
        if (Path.IsPathRooted(fromWorkspace) ||
            fromWorkspace.Equals("..", comparison) ||
            fromWorkspace.StartsWith(".." + Path.DirectorySeparatorChar, comparison) ||
            fromWorkspace.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Caminho fora do workspace.");

        var cursor = basePath;
        foreach (var segment in fromWorkspace.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment); FileSystemInfo info = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);
            if (info.Exists && info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(true)?.FullName ?? throw new InvalidOperationException("Link simbólico inválido.");
                var rel = Path.GetRelativePath(basePath, resolved);
                if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException("Link simbólico aponta para fora do workspace.");
            }
        }

        return full;
    }
    static string ListFiles(string path) => !Directory.Exists(path) ? "Diretório não encontrado." : string.Join("\n", Directory.EnumerateFileSystemEntries(path).Where(p => !Ignored(p)).Take(300).Select(p => (Directory.Exists(p) ? "[dir] " : "[file] ") + Path.GetFileName(p)));
    static string Search(string root, string query) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => !Ignored(p)).SelectMany(p => Find(p, query)).Take(200));
    static IEnumerable<string> Find(string path, string query) { IEnumerable<string> lines; try { lines = File.ReadLines(path); } catch { yield break; } var i = 0; foreach (var line in lines) { i++; if (line.Contains(query, StringComparison.OrdinalIgnoreCase)) yield return $"{Path.GetRelativePath(Directory.GetCurrentDirectory(), path)}:{i}: {line}"; } }
    static bool Ignored(string path) { var value = path.Replace('\\', '/'); return value.Contains("/.git/") || value.Contains("/bin/") || value.Contains("/obj/") || value.Contains("/node_modules/"); }
    static async Task<string> Run(string root, string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct); await process.WaitForExitAsync(ct);
        var output = await stdout; var error = await stderr; if (process.ExitCode != 0) throw new InvalidOperationException($"Comando falhou ({process.ExitCode}): {error.Trim()}");
        return output + (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error);
    }
}
