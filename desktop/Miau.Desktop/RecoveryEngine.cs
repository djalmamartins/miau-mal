using System.Security.Cryptography;
using System.Text;

namespace Miau.Desktop;

public sealed record RecoveryContext(string Objective, string Subobjective, IReadOnlyList<string> PendingCriteria,
    string RelevantFile, string LastError, IReadOnlyList<string> StagnantActions, int Level)
{
    public string ToPrompt()
    {
        var stages = Level >= 3 ? "\nETAPAS OBRIGATÓRIAS: 1 estrutura HTML; 2 identidade/conteúdo; 3 CSS/layout; 4 responsividade; 5 comportamento; 6 render desktop; 7 render mobile; 8 inspeção visual; 9 correções; 10 validação final. Execute somente a próxima etapa pendente." : "";
        return $"RECOVERY CONTEXT (compacto)\nObjetivo: {Objective}\nSubobjetivo: {Subobjective}\nArquivo: {RelevantFile}\nCritérios pendentes: {string.Join(", ", PendingCriteria)}\nÚltimo sinal: {LastError}\nAções sem progresso: {string.Join(" → ", StagnantActions)}\nNível: {Level}{stages}\nMude materialmente a estratégia. Não repita conteúdo ou argumentos equivalentes.";
    }
}

public sealed record RecoveryDecision(int Level, bool Stop, string Message, RecoveryContext Context, string IntentFingerprint);

public sealed class RecoveryEngine(int maximumLevel = 4)
{
    readonly Dictionary<string, int> ineffectiveByIntent = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> stagnantActions = [];
    public int NoEffectiveChangeCount { get; private set; }
    public int RecoveryCount { get; private set; }
    public bool HasUnresolvedStagnation => ineffectiveByIntent.Values.Any(x => x > 0);
    public bool HasBlockingStagnation => ineffectiveByIntent.Values.Any(x => x >= 3);

    public RecoveryDecision ObserveNoEffectiveChange(string objective, MiauAction action, ToolResult result, AcceptancePlan acceptance)
    {
        var target = action.Arguments.GetValueOrDefault("path", result.Metadata.GetValueOrDefault("target_path", "workspace"));
        var criterion = ClosestCriterion(action, acceptance);
        // Group broad implementation attempts by objective/criterion rather than only by tool/path.
        // This detects A→B→C no-change cycles that pursue the same unfinished outcome.
        var intent = Hash(Normalize(objective) + "|" + criterion);
        var level = Math.Min(maximumLevel, ineffectiveByIntent.GetValueOrDefault(intent) + 1);
        ineffectiveByIntent[intent] = level; NoEffectiveChangeCount++; RecoveryCount++;
        var effective = result.Metadata.GetValueOrDefault("effective_change", "unknown");
        var oldHash = result.Metadata.GetValueOrDefault("old_hash", "unknown");
        var proposedHash = result.Metadata.GetValueOrDefault("proposed_hash", "unknown");
        stagnantActions.Add($"{action.Action}:{target}:effective={effective}:old={oldHash}:proposed={proposedHash}");
        if (stagnantActions.Count > 8) stagnantActions.RemoveAt(0);
        var pending = acceptance.Criteria.Where(x => x.Required && x.Status == AcceptanceStatus.Pending).Select(x => x.Description).ToArray();
        var context = new RecoveryContext(Trim(objective, 1200), criterion, pending, target,
            "NoEffectiveChange: conteúdo proposto não altera o workspace", stagnantActions.TakeLast(6).ToArray(), level);
        var message = level switch
        {
            1 => "A alteração proposta é idêntica ao arquivo atual. Nenhum progresso foi produzido. Não repita a mesma solução. Compare o estado atual com o objetivo e produza uma alteração material.",
            2 => "A segunda tentativa equivalente também não produziu mudança. O histórico será compactado e a estratégia deve mudar.",
            3 => "A estagnação persistiu. A tarefa foi decomposta e deve avançar uma etapa objetiva por vez.",
            _ => $"Estagnação persistente interrompida com segurança após {NoEffectiveChangeCount} operações sem alteração efetiva."
        };
        return new(level, level >= maximumLevel, message, context, intent);
    }

    public bool RecordEffectiveProgress(MiauAction action, ToolResult result)
    {
        if (!result.Success || result.Metadata.GetValueOrDefault("effective_change") != "true") return false;
        if (!HasUnresolvedStagnation) return false;
        ineffectiveByIntent.Clear(); stagnantActions.Clear();
        return true;
    }

    public string Diagnostic(string objective, AcceptancePlan acceptance) =>
        $"Objetivo: {Trim(objective, 600)}\nÚltima etapa: recovery nível {Math.Min(maximumLevel, ineffectiveByIntent.Values.DefaultIfEmpty(0).Max())}\nNoEffectiveChange: {NoEffectiveChangeCount}\nOperações: {string.Join(" → ", stagnantActions)}\nCritérios pendentes: {acceptance.PendingSummary()}\nMotivo: estratégias equivalentes persistiram sem produzir task delta.\nArquivos existentes foram preservados.";

    static string ClosestCriterion(MiauAction action, AcceptancePlan acceptance)
    {
        var reason = action.Reason ?? "";
        return acceptance.Criteria.FirstOrDefault(x => reason.Contains(x.Id, StringComparison.OrdinalIgnoreCase) || reason.Contains(x.Description, StringComparison.OrdinalIgnoreCase))?.Id
            ?? acceptance.Criteria.FirstOrDefault(x => x.Required && x.Status == AcceptanceStatus.Pending)?.Id ?? "task-change";
    }
    static string Normalize(string value) => string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
