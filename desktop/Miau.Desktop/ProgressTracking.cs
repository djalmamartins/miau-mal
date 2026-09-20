using System.Security.Cryptography;
using System.Text;

namespace Miau.Desktop;

public enum ProgressKind { Inspection, Change, Diff, Validation, Criterion, Visual }
public sealed record ProgressEvent(ProgressKind Kind, string Evidence, DateTimeOffset Timestamp);
public sealed record ProgressSnapshot(int Version, IReadOnlyList<ProgressEvent> Events, IReadOnlyList<string> RecentActions, string StateFingerprint);
public sealed record StagnationResult(bool Detected, int Level, string Cycle, string Message);

public sealed class ProgressTracker
{
    readonly HashSet<string> evidence = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ProgressEvent> events = [];
    readonly List<(string Action, int Version)> actions = [];
    int level;
    public ProgressSnapshot Snapshot => new(events.Count, events.ToArray(), actions.TakeLast(12).Select(x => x.Action).ToArray(), Hash(string.Join('|', evidence.Order())));

    public bool Record(ProgressKind kind, string value)
    {
        var key = $"{kind}:{Normalize(value)}";
        if (!evidence.Add(key)) return false;
        events.Add(new(kind, value, DateTimeOffset.Now));
        return true;
    }

    public StagnationResult ObserveAction(MiauAction action)
    {
        var signature = Fingerprint(action); actions.Add((signature, events.Count));
        if (actions.Count > 24) actions.RemoveAt(0);
        var recent = actions.TakeLast(12).ToArray();
        var stagnant = recent.Select(x => x.Action).ToArray();
        for (var size = 1; size <= 4; size++)
        {
            if (stagnant.Length < size * 2) continue;
            var tail = stagnant[^size..]; var previous = stagnant[^(size * 2)..^size];
            if (!tail.SequenceEqual(previous) || recent[^size..].Any(x => x.Version != events.Count)) continue;
            level = Math.Min(4, level + 1);
            var cycle = string.Join(" → ", tail);
            return new(true, level, cycle, $"Atividade sem progresso: ciclo {cycle} detectado.");
        }
        return new(false, level, "", "");
    }

    public static string Fingerprint(MiauAction action) => action.Action + ":" + string.Join(';', action.Arguments.OrderBy(x => x.Key).Select(x => $"{x.Key}={Normalize(x.Value)}"));
    public static string ArtifactHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string Hash(string value) => ArtifactHash(Encoding.UTF8.GetBytes(value));
    static string Normalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
