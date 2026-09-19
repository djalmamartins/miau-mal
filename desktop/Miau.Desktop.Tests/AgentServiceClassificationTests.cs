using System.Reflection;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class AgentServiceClassificationTests
{
    static JobRequirements Classify(string prompt)
    {
        var method = typeof(AgentService).GetMethod("Classify", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (JobRequirements)method.Invoke(null, [prompt])!;
    }

    [Fact]
    public void CreateTaskWithScopeGuardIsMutable()
    {
        var r = Classify("Crie no projeto um arquivo chamado miau-runtime-test.txt. Não altere nenhum outro arquivo.");
        Assert.True(r.RequiresChange);
        Assert.False(r.ReadOnly);
    }

    [Fact]
    public void ExplicitAnalysisOnlyTaskIsReadOnly()
    {
        var r = Classify("Apenas analise o projeto. Não altere nenhum arquivo.");
        Assert.False(r.RequiresChange);
        Assert.True(r.ReadOnly);
    }
}
