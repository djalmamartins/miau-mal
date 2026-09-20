namespace Miau.Desktop;

public sealed class ExperienceHarvester
{
    public TrainingRecord Harvest(string workspace, string model, string task, IReadOnlyList<string> plan,
        IReadOnlyList<TaskTraceEvent> trace, JobEvidence evidence, string finalSummary, TimeSpan duration, string origin = "interactive")
    {
        var actions = trace.Where(x => x.Kind == "action_requested").ToArray();
        var results = trace.Where(x => x.Kind == "tool").ToArray();
        return new TrainingRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, Miau1Coder.AgentVersion, model, DatasetService.Fingerprint(workspace),
            task, plan, actions, results, evidence.FilesInspected, evidence.FilesChanged,
            actions.Where(x => x.Name is ToolNames.ApplyPatch or ToolNames.ReplaceInFile or ToolNames.WriteFile).Select(x => x.Detail).ToArray(),
            results.LastOrDefault(x => x.Name == ToolNames.Build)?.Detail, results.LastOrDefault(x => x.Name == ToolNames.Test)?.Detail,
            trace.Where(x => !x.Success).Select(x => x.Detail).ToArray(), evidence.Attempts, finalSummary)
        {
            Duration = duration, Success = true,
            RecoveryStrategy = trace.Any(x => x.Kind == "recovery_success") ? "NoEffectiveChange diagnosticado; contexto compactado; estratégia diferente produziu task delta" : evidence.Attempts > 0 ? "reinspeção e mudança de ação após falha" : null,
            Origin = origin, HumanIntervention = false, FinalBuildPassed = !evidence.ValidationRan || evidence.ValidationPassed
        };
    }
}
