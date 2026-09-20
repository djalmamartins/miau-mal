namespace Miau.Desktop;

public sealed record WebReferenceResult(bool Available, string? Content = null, string? ContentType = null, string? Reason = null);
public interface IWebReferenceProvider { Task<WebReferenceResult> FetchAsync(Uri uri, CancellationToken ct); }

public sealed class HttpReferenceProvider : IWebReferenceProvider
{
    public async Task<WebReferenceResult> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; client.DefaultRequestHeaders.UserAgent.ParseAdd("MIAU1-Coder/0.1");
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return new(false, Reason: $"HTTP {(int)response.StatusCode}");
            return new(true, await response.Content.ReadAsStringAsync(timeout.Token), response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, Reason: "timeout"); }
        catch (HttpRequestException ex) { return new(false, Reason: DatasetService.Redact(ex.Message)); }
    }
}
