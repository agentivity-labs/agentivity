using Agentivity.Infrastructure.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

/// <summary>
/// Gives the capability registry the meaning of texts with the platform's local, offline model (all-MiniLM-L6-v2). The model
/// is loaded on demand from the background, because loading it may mean downloading it; a request never waits for that.
/// </summary>
public sealed class LocalCapabilityEmbedder(IServiceProvider services, ILogger<LocalCapabilityEmbedder>? logger = null) : ICapabilityEmbedder
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private volatile LocalEmbeddingService? _service;
    private volatile bool _attempted;

    public string ModelId => "all-MiniLM-L6-v2";

    public bool IsAvailable => _service?.IsAvailable == true;

    public async Task<bool> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_attempted)
            return IsAvailable;

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_attempted)
                return IsAvailable;

            try
            {
                // Resolving the service is what loads (and, if need be, downloads) the model: do it off the caller's thread.
                _service = await Task.Run(() => services.GetService<LocalEmbeddingService>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "The local embedding model could not be loaded; capability search stays on words");
            }

            _attempted = true;
            return IsAvailable;
        }
        finally
        {
            _loading.Release();
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false) || _service is null)
            throw new InvalidOperationException("The local embedding model is not available.");

        var vectors = await _service.CreateBatchEmbeddingsAsync(null!, texts, cancellationToken).ConfigureAwait(false);
        return vectors;
    }
}
