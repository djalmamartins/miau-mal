using System.Net.Http.Json;
using System.Text.Json;

namespace Miau.Desktop;

public sealed class OllamaModelAdapter : IModelAdapter
{
    readonly HttpClient http;
    readonly TimeSpan requestTimeout;
    public OllamaModelAdapter(string modelId, HttpClient? client = null, TimeSpan? requestTimeout = null)
    { ModelId = modelId; http = client ?? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = Timeout.InfiniteTimeSpan }; this.requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5); }
    public string ModelId { get; }
    public async Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(requestTimeout);
        var messages = new[] { new { role = "system", content = request.SystemPrompt } }
            .Concat(request.Turns.Select(x => new { role = x.Role, content = x.Content })).ToArray();
        try
        {
            using var response = await http.PostAsJsonAsync("/api/chat", new { model = ModelId, messages, stream = false, format = "json", options = new { temperature = .1 } }, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return document.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException($"Ollama não respondeu em {requestTimeout}."); }
    }
}

public sealed class MiauCoderAdapter : IModelAdapter
{
    readonly IModelAdapter inner;
    public MiauCoderAdapter(IModelAdapter inner) => this.inner = inner;
    public string ModelId => "miau1-coder";
    public Task<string> CompleteStepAsync(ModelRequest request, CancellationToken ct) => inner.CompleteStepAsync(request, ct);
}
