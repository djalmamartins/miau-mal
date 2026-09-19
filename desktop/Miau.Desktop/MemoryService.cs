using System.Text.Json;

namespace Miau.Desktop;

public enum MemoryCategory { Architecture, Decision, Solution, Failure, Convention, Command, Preference }
public sealed record ProjectMemory(string Id, DateTimeOffset Timestamp, string ProjectFingerprint, MemoryCategory Category,
    string Subject, string Content, IReadOnlyList<string> Files, IReadOnlyList<string> Tags, string SourceTask, double Confidence, int Uses = 0);

public sealed class MemoryService
{
    readonly string baseDir;
    public MemoryService(string? baseDir = null) => this.baseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "memory");

    public Task RememberAsync(string root, string prompt, string summary, CancellationToken ct) =>
        RememberAsync(root, new ProjectMemory(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, DatasetService.Fingerprint(root), MemoryCategory.Solution,
            Subject(prompt), Compact(summary), [], Terms(prompt).Take(8).ToArray(), prompt, .75), ct);

    public async Task RememberAsync(string root, ProjectMemory memory, CancellationToken ct)
    {
        await MigrateLegacyAsync(root, ct);
        var dir = ProjectDirectory(root); Directory.CreateDirectory(dir); var file = Path.Combine(dir, memory.Category.ToString().ToLowerInvariant() + ".jsonl");
        var existing = await ReadAll(file, ct);
        var duplicate = existing.FirstOrDefault(x => Similar(x, memory));
        if (duplicate is not null)
        {
            var merged = duplicate with { Timestamp = DateTimeOffset.Now, Confidence = Math.Max(duplicate.Confidence, memory.Confidence), Uses = duplicate.Uses + 1,
                Files = duplicate.Files.Concat(memory.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Tags = duplicate.Tags.Concat(memory.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            existing[existing.IndexOf(duplicate)] = merged;
            await File.WriteAllLinesAsync(file, existing.Select(x => JsonSerializer.Serialize(x)), ct); return;
        }
        await File.AppendAllTextAsync(file, JsonSerializer.Serialize(memory) + Environment.NewLine, ct);
    }

    public async Task<string> RecallAsync(string root, string prompt, CancellationToken ct)
    {
        await MigrateLegacyAsync(root, ct);
        var dir = ProjectDirectory(root); if (!Directory.Exists(dir)) return ""; var terms = Terms(prompt).ToArray();
        var memories = new List<ProjectMemory>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl")) memories.AddRange(await ReadAll(file, ct));
        var ranked = memories.Select(x => (Memory: x, Score: Rank(x, terms))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Memory.Timestamp).Take(6)
            .Select(x => $"- [{x.Memory.Category}] {x.Memory.Subject}: {Compact(x.Memory.Content)}");
        return string.Join("\n", ranked);
    }
    public string ProjectDirectory(string root) => Path.Combine(baseDir, DatasetService.Fingerprint(root));
    async Task MigrateLegacyAsync(string root, CancellationToken ct)
    {
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var legacy = Path.Combine(baseDir, new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()) + ".jsonl");
        var marker = Path.Combine(ProjectDirectory(root), ".legacy-migrated"); if (!File.Exists(legacy) || File.Exists(marker)) return;
        Directory.CreateDirectory(ProjectDirectory(root));
        foreach (var line in await File.ReadAllLinesAsync(legacy, ct))
        {
            try
            {
                using var doc = JsonDocument.Parse(line); var x = doc.RootElement;
                var prompt = x.TryGetProperty("Prompt", out var p) ? p.GetString() ?? "legacy" : "legacy";
                var summary = x.TryGetProperty("Summary", out var s) ? s.GetString() ?? "" : "";
                var item = new ProjectMemory(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, DatasetService.Fingerprint(root), MemoryCategory.Solution, Subject(prompt), summary, [], Terms(prompt).Take(8).ToArray(), prompt, .6);
                await File.AppendAllTextAsync(Path.Combine(ProjectDirectory(root), "solution.jsonl"), JsonSerializer.Serialize(item) + Environment.NewLine, ct);
            }
            catch { }
        }
        await File.WriteAllTextAsync(marker, DateTimeOffset.Now.ToString("O"), ct);
    }
    static double Rank(ProjectMemory memory, string[] terms) => terms.Count(t => memory.Subject.Contains(t, StringComparison.OrdinalIgnoreCase) || memory.Content.Contains(t, StringComparison.OrdinalIgnoreCase) || memory.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) * 2 + memory.Confidence + Math.Min(memory.Uses, 5) * .2 + (DateTimeOffset.Now - memory.Timestamp < TimeSpan.FromDays(30) ? .5 : 0);
    static bool Similar(ProjectMemory a, ProjectMemory b) => a.Category == b.Category && (a.Subject.Equals(b.Subject, StringComparison.OrdinalIgnoreCase) || Terms(a.Content).Intersect(Terms(b.Content), StringComparer.OrdinalIgnoreCase).Take(5).Count() >= 5);
    static async Task<List<ProjectMemory>> ReadAll(string file, CancellationToken ct) { if (!File.Exists(file)) return []; var lines = await File.ReadAllLinesAsync(file, ct); return lines.Select(x => { try { return JsonSerializer.Deserialize<ProjectMemory>(x); } catch { return null; } }).Where(x => x is not null).Cast<ProjectMemory>().ToList(); }
    static IEnumerable<string> Terms(string value) => value.Split([' ', '\r', '\n', '\t', ',', '.', ';', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).Select(x => x.ToLowerInvariant()).Distinct();
    static string Subject(string prompt) => prompt.Length > 100 ? prompt[..100] : prompt;
    static string Compact(string value) => value.Length > 1200 ? value[..1200] + "…" : value;
}
