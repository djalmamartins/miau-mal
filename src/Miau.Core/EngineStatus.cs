namespace Miau.Core;

public enum EngineState
{
    Unloaded,
    Loading,
    Ready,
    Generating,
    Unloading,
    Faulted
}

public sealed record EngineStatus(EngineState State, ModelDescriptor? Model = null, string? Error = null);
