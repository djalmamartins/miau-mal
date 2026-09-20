using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class CompletionGateTests
{
    [Fact] public void FinalWithoutTaskDeltaIsRejected()
    {
        var decision = CompletionGate.BeforeValidation(new(true, false), new AcceptancePlan([]), []);
        Assert.False(decision.Allowed); Assert.Contains("Nenhuma alteração efetiva", decision.Reason);
    }

    [Fact] public void FinalWithZeroAcceptanceCriteriaIsRejected()
    {
        var decision = CompletionGate.BeforeValidation(new(true, false, true, true), new AcceptancePlan([]), ["index.html"]);
        Assert.False(decision.Allowed); Assert.Contains("Nenhum critério", decision.Reason);
    }

    [Fact] public void PendingStructuralCriterionBlocksCompletion()
    {
        var plan = new AcceptancePlan([new("hero", "hero", AcceptanceType.Structural, true)]);
        var decision = CompletionGate.BeforeValidation(new(true, false), plan, ["index.html"]);
        Assert.False(decision.Allowed); Assert.Contains("hero", decision.Reason);
    }

    [Fact] public void EffectiveDeltaWithSatisfiedStructureAllowsValidation()
    {
        var plan = new AcceptancePlan([new("hero", "hero", AcceptanceType.Structural, true, AcceptanceStatus.Satisfied)]);
        Assert.True(CompletionGate.BeforeValidation(new(true, false), plan, ["index.html"]).Allowed);
    }
}
