namespace Miau.Desktop;

public sealed class VisualRevisionPolicy(int maximum = 3)
{
    readonly HashSet<string> seen = [];
    public int Revisions { get; private set; }
    public bool CanRevise(string screenshotHash, string criteriaFingerprint, out string reason)
    {
        var state = screenshotHash + ":" + criteriaFingerprint;
        if (!seen.Add(state)) { reason = "Screenshot e critérios são idênticos à revisão anterior; não houve progresso visual."; return false; }
        Revisions++;
        if (Revisions > maximum) { reason = $"Limite de {maximum} revisões visuais atingido."; return false; }
        reason = ""; return true;
    }
}
