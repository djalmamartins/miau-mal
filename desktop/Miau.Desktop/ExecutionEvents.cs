namespace Miau.Desktop;

public enum ExecutionEventType
{
    JobStarted, PhaseChanged, ModelRequestStarted, ModelRequestCompleted, ToolStarted, ToolCompleted, ToolFailed,
    FileRead, FileCreated, FileChanged, CommandStarted, CommandCompleted, CommandFailed, DiffStarted, DiffCompleted,
    BuildStarted, BuildCompleted, BuildFailed, TestsStarted, TestsCompleted, TestsFailed, RetryStarted,
    JobCompleted, JobFailed, JobCancelled
}

public sealed record ExecutionEvent(
    DateTimeOffset Timestamp,
    ExecutionEventType Type,
    JobPhase Phase,
    string Description,
    string? Target = null,
    TimeSpan? Duration = null,
    bool? Success = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? Details = null);

public static class Miau1Coder
{
    public const string AgentName = "MIAU1-Coder v0";
    public const string AgentVersion = "0.1.0";
}
