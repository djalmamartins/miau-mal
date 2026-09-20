using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ExecutionProgressNarrativeTests
{
    [Fact] public void UnavailableReferenceDoesNotClaimItWasAnalyzed()
    {
        var ev = new ExecutionEvent(DateTimeOffset.Now, ExecutionEventType.ToolCompleted, JobPhase.Inspecting, "Acessou referência web", Metadata: new Dictionary<string, string> { ["reference_status"] = "unavailable" });
        var text = ExecutionProgressNarrative.For(ev);
        Assert.Contains("não ficou disponível", text); Assert.DoesNotContain("analisada", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void PolicyRecoveryIsPresentedAsStrategyAdjustment()
    {
        var text = ExecutionProgressNarrative.For(new(DateTimeOffset.Now, ExecutionEventType.PolicyRecovery, JobPhase.Planning, "Final prematuro rejeitado", Success: true));
        Assert.Contains("ajustando", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("falhou", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void CompletionClaimsOnlyCollectedEvidence()
    {
        var text = ExecutionProgressNarrative.For(new(DateTimeOffset.Now, ExecutionEventType.JobCompleted, JobPhase.Completed, "Tarefa concluída", Success: true));
        Assert.Contains("evidências obrigatórias", text);
    }
}
