using System.Text.Json;

namespace Miau.Desktop;

public sealed record ConversationEntry(string SessionId, DateTimeOffset At, string Role, string Text, string? Workspace);

public sealed class ConversationService
{
    readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MIAU", "conversations");
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");

    public void NewSession() => SessionId = Guid.NewGuid().ToString("N");

    public async Task AppendAsync(string role, string text, string? workspace)
    {
        Directory.CreateDirectory(dir);
        var entry = new ConversationEntry(SessionId, DateTimeOffset.Now, role, text, workspace);
        await File.AppendAllTextAsync(Path.Combine(dir, SessionId + ".jsonl"), JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    public IEnumerable<(string Id, DateTimeOffset At, string Preview)> Recent(int take=20)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(take))
        {
            ConversationEntry? first = null;
            try { first = JsonSerializer.Deserialize<ConversationEntry>(File.ReadLines(file).FirstOrDefault() ?? ""); } catch { }
            if (first is not null) yield return (Path.GetFileNameWithoutExtension(file), first.At, first.Text);
        }
    }

    public IEnumerable<ConversationEntry> Read(string id)
    {
        var file=Path.Combine(dir,id+".jsonl");
        if(!File.Exists(file)) yield break;
        foreach(var line in File.ReadLines(file))
        {
            ConversationEntry? item=null;
            try { item=JsonSerializer.Deserialize<ConversationEntry>(line); } catch { }
            if(item is not null) yield return item;
        }
    }
}
