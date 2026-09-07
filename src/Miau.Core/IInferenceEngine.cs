namespace Miau.Core;

/// <summary>Owns one loaded model. Implementations must propagate cancellation and release owned resources.</summary>
public interface IInferenceEngine : IAsyncDisposable
{
    Task LoadModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default);
    Task UnloadModelAsync(CancellationToken cancellationToken = default);
    Task<string> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken = default);
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
