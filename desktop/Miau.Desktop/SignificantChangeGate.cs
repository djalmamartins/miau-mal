namespace Miau.Desktop;

public sealed record SignificantChangeDecision(bool Passed, int Signals, string Reason);

public static class SignificantChangeGate
{
    public static SignificantChangeDecision Evaluate(string task, string root, WorkspaceBaseline baseline, AcceptancePlan acceptance)
    {
        if (!AcceptancePlanner.IsSignificantImprovement(task)) return new(true, 0, "Mudança significativa não foi solicitada.");
        var delta = baseline.ChangesProducedNow(root); if (delta.Count == 0) return new(false, 0, "O pedido exige melhoria significativa, mas não existe task delta.");
        var signals = 0;
        foreach (var path in delta)
        {
            var full = Path.Combine(root, path); var before = baseline.TextFiles.GetValueOrDefault(path, ""); var after = File.Exists(full) ? SafeRead(full) : "";
            var ext = Path.GetExtension(path);
            if (ext is ".html" or ".htm")
            {
                if (Normalize(before) != Normalize(after)) signals++;
                foreach (var token in new[] { "header", "hero", "product", "editorial", "cta", "footer" })
                    if (Feature(before, token) != Feature(after, token)) signals++;
            }
            else if (ext.Equals(".css", StringComparison.OrdinalIgnoreCase))
            {
                if (Normalize(before) != Normalize(after)) signals++;
                if (before.Contains("@media", StringComparison.OrdinalIgnoreCase) != after.Contains("@media", StringComparison.OrdinalIgnoreCase) || Math.Abs(after.Length - before.Length) > 120) signals++;
            }
            else if (Normalize(before) != Normalize(after)) signals++;
        }
        var modifiedCriteria = acceptance.Criteria.Count(x => x.RequiresTaskDelta && x.Status == AcceptanceStatus.Satisfied);
        if (modifiedCriteria >= 3) signals += 2;
        return signals >= 3 ? new(true, signals, $"Mudança material comprovada por {signals} sinais estruturais.") : new(false, signals, $"A alteração produziu somente {signals} sinal(is) estrutural(is); o pedido exige redesign material em múltiplos elementos.");
    }
    static string Feature(string value, string token)
    {
        var index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase); if (index < 0) return "missing";
        var start = Math.Max(0, index - 120); var length = Math.Min(value.Length - start, 500); return Normalize(value.Substring(start, length));
    }
    static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return ""; } }
}
