using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ProtocolTests
{
    [Fact] public void ValidActionParses() { Assert.True(BrainResponse.TryParse("""{"type":"action","action":"read_file","arguments":{"path":"a.cs"},"reason":"inspect"}""", out var value, out _)); Assert.Equal(ToolNames.ReadFile, value!.Action!.Action); }
    [Fact] public void InvalidJsonIsRejected() => Assert.False(BrainResponse.TryParse("{", out _, out _));
    [Fact] public void ProseIsNotAnAction() => Assert.False(BrainResponse.TryParse("Vou editar agora.", out _, out _));
    [Fact] public void UnknownToolIsRejected() => Assert.False(BrainResponse.TryParse("""{"type":"action","action":"delete_everything","arguments":{}}""", out _, out _));
}
