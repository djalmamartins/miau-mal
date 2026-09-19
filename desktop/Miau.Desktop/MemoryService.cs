using System.Text.Json;

namespace Miau.Desktop;

public sealed record MemoryEntry(DateTimeOffset At, string Project, string Prompt, string Summary);

public sealed class MemoryService
{
    static string BaseDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "memory");

    public async Task RememberAsync(string root, string prompt, string summary, CancellationToken ct)
    {
        Directory.CreateDirectory(BaseDir);
        var project = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var file = Path.Combine(BaseDir, Safe(project) + ".jsonl");
        var entry = new MemoryEntry(DateTimeOffset.Now, project, prompt, summary);
        await File.AppendAllTextAsync(file, JsonSerializer.Serialize(entry) + Environment.NewLine, ct);
    }

    public async Task<string> RecallAsync(string root, string prompt, CancellationToken ct)
    {
        var project = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var file = Path.Combine(BaseDir, Safe(project) + ".jsonl");
        if (!File.Exists(file)) return "";
        var terms = prompt.Split([' ','\r','\n','\t',',','.',';',':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lines = await File.ReadAllLinesAsync(file, ct);
        var ranked = lines.Reverse().Select(line => { try { return JsonSerializer.Deserialize<MemoryEntry>(line); } catch { return null; } })
            .Where(x => x is not null)
            .Select(x => (Entry:x!, Score:terms.Count(t => x!.Prompt.Contains(t,StringComparison.OrdinalIgnoreCase)||x.Summary.Contains(t,StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x=>x.Score).ThenByDescending(x=>x.Entry.At).Take(5).Where(x=>x.Score>0).Select(x=>$"- {x.Entry.At:yyyy-MM-dd}: {Trim(x.Entry.Summary)}");
        return string.Join("\n", ranked);
    }

    static string Safe(string s)=>new(s.Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_'?c:'-').ToArray());
    static string Trim(string s)=>s.Length>800?s[..800]+"…":s;
}
