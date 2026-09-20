namespace Miau.Desktop;

public enum ExecutionKind { Interactive, Training, Benchmark, SelfRepair, AutonomousJob }
public sealed class ExecutionCoordinator
{
    readonly object gate = new(); readonly Dictionary<string, ExecutionKind> active = new(StringComparer.OrdinalIgnoreCase);
    public bool TryAcquire(string workspace, ExecutionKind kind, out IDisposable? lease)
    {
        lock (gate)
        {
            if (active.ContainsKey(Path.GetFullPath(workspace))) { lease = null; return false; }
            if (kind != ExecutionKind.Interactive && active.Values.Contains(ExecutionKind.Interactive)) { lease = null; return false; }
            active[Path.GetFullPath(workspace)] = kind; lease = new Lease(this, Path.GetFullPath(workspace)); return true;
        }
    }
    void Release(string workspace) { lock (gate) active.Remove(workspace); }
    sealed class Lease(ExecutionCoordinator owner, string workspace) : IDisposable { bool disposed; public void Dispose() { if (!disposed) { disposed = true; owner.Release(workspace); } } }
}

public static class ExecutionCoordination { public static ExecutionCoordinator Shared { get; } = new(); }
