using System.Diagnostics;

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
    static readonly string[] Dangerous = ["git reset --hard", "git clean", "git push --force", "git push -f", "rm -rf", "rmdir /s", "del /f /s", "format ", "shutdown", "reboot"];
    public async Task<ToolResult> ExecuteAsync(string workspace, MiauAction action, bool readOnly, CancellationToken ct)
    {
        try
        {
            if (readOnly && action.Action is ToolNames.WriteFile or ToolNames.ReplaceInFile or ToolNames.ApplyPatch or ToolNames.RunCommand or ToolNames.Build or ToolNames.Test)
                throw new InvalidOperationException("A tarefa é somente leitura; ferramentas mutáveis estão bloqueadas.");
            string Arg(string name, string fallback = "") => action.Arguments.TryGetValue(name, out var value) ? value : fallback;
            return action.Action switch
            {
                ToolNames.ListFiles => ToolResult.Ok(action.Action, ListFiles(SafePath(workspace, Arg("path", "."))), ("inspected_path", Arg("path", "."))),
                ToolNames.ReadFile => ToolResult.Ok(action.Action, await File.ReadAllTextAsync(SafePath(workspace, Arg("path")), ct), ("inspected_path", Arg("path"))),
                ToolNames.Search => ToolResult.Ok(action.Action, Search(workspace, Arg("query")), ("inspected_path", ".")),
                ToolNames.WriteFile => await Write(action.Action, SafePath(workspace, Arg("path")), Arg("content"), Arg("path"), ct),
                ToolNames.ReplaceInFile => await Replace(action.Action, SafePath(workspace, Arg("path")), Arg("old_text"), Arg("new_text"), Arg("path"), ct),
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

    public async Task<ToolResult> ValidateAsync(string workspace, CancellationToken ct)
    {
        var project = Directory.EnumerateFiles(workspace, "*.sln*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? Directory.EnumerateFiles(workspace, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
        if (project is not null)
        {
            var relative = Path.GetRelativePath(workspace, project).Replace("\"", "\\\"");
            return await Validation(ToolNames.Build, workspace, $"dotnet build \"{relative}\"", ct);
        }
        if (File.Exists(Path.Combine(workspace, "package.json"))) return await Validation(ToolNames.Test, workspace, "npm test -- --run", ct);
        return ToolResult.Ok(ToolNames.Test, "Nenhum build/teste automático reconhecido; validação não aplicável.", ("validation", "true"));
    }

    static async Task<ToolResult> Write(string tool, string path, string content, string relative, CancellationToken ct)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, content, ct); return ToolResult.Ok(tool, $"Arquivo salvo: {relative}", ("changed_path", relative)); }
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

        var first = current.IndexOf(oldText, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, current[..first] + newText + current[(first + oldText.Length)..], ct);
        return ToolResult.Ok(tool, $"Trecho alterado: {relative}", ("changed_path", relative));
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
        try { await File.WriteAllTextAsync(temp, patch, ct); var output = await Run(workspace, "git", ["apply", "--whitespace=nowarn", temp], ct); return ToolResult.Ok(tool, output, ("changed_path", "(patch)")); }
        finally { File.Delete(temp); }
    }
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
