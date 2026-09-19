using System.Net;
using Miau.Desktop;
using Xunit;

namespace Miau.Desktop.Tests;

public sealed class ModelAdapterTests
{
    [Fact]
    public async Task TimeoutIsReportedSeparately()
    {
        var client = new HttpClient(new SlowHandler()) { BaseAddress = new Uri("http://localhost") };
        var adapter = new OllamaModelAdapter("model", client, TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => adapter.CompleteStepAsync(new("prompt", []), default));
    }
    sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new(HttpStatusCode.OK); }
    }
}
