using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Workflow.Catalog.AI.Models;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

/// <summary>
/// Shared local ONNX embedding model (all-MiniLM-L6-v2) — loaded once and reused by both
/// <c>EmbeddingsLocalNode</c> (the graph-visible resource node) and every memory/vector-store
/// node's zero-configuration default when no other embedding is bundled or connected. A DI
/// singleton (not a <c>Node</c> itself) specifically so ordinary nodes can constructor-inject it —
/// <c>Node</c> subtypes are only registered under the open <c>Node</c> service type (see
/// <c>CatalogPlugin.ConfigureServices</c> / <c>ServiceCollectionExtensions.AddSingleton(typeof(Node), nodeType)</c>),
/// so a concrete node type can never be resolved as itself.
/// </summary>
public sealed class LocalEmbeddingService : IEmbeddingProvider, ITextEmbedder, IDisposable
{
    private readonly OnnxLocalEmbeddingCapability? _onnx;

    /// <summary>False when the model could not be found or downloaded — callers should surface a
    /// clear error rather than let <see cref="CreateEmbeddingAsync"/> throw an opaque one.</summary>
    public bool IsAvailable => _onnx is not null;

    public LocalEmbeddingService(IOptions<LLMModelsOptions> modelsOptions, ILogger<LocalEmbeddingService> logger)
        : this(modelsOptions, logger, network: null) { }

    /// <summary>[network] stands for the internet in tests: the download goes through it.</summary>
    internal LocalEmbeddingService(IOptions<LLMModelsOptions> modelsOptions, ILogger<LocalEmbeddingService> logger, HttpMessageHandler? network)
    {
        ArgumentNullException.ThrowIfNull(logger);

        string modelsBasePath = PathResolver.Resolve(modelsOptions.Value.StoragePath);
        string onnxModelPath = Path.Combine(modelsBasePath, "sentence-transformers", "all-MiniLM-L6-v2", "model.onnx");

        if (!File.Exists(onnxModelPath))
        {
            logger.LogInformation("ONNX model not found, attempting download from HuggingFace...");
            string? why = OnnxModelDownloader.DownloadSentenceTransformerAsync("all-MiniLM-L6-v2", modelsBasePath, logger, network).GetAwaiter().GetResult();

            if (why is not null)
                logger.LogWarning("The free local embedding model could not be downloaded: {Why}. Everything else works; what uses this model (Knowledge Bases, Local Embeddings, a vector store with no Embeddings connected) will say so when it runs. The download is tried again at the next start.", why);
        }

        if (File.Exists(onnxModelPath))
        {
            _onnx = new OnnxLocalEmbeddingCapability(onnxModelPath, logger);
            logger.LogInformation("Local ONNX embedding model loaded (100% offline).");
        }
    }

    /// <summary>Test-only constructor — skips the model lookup/download entirely so unit tests
    /// that exercise a node's bundled-credentials or external-port path (never the local fallback
    /// itself) don't need a real ONNX model file or network access just to construct the node.</summary>
    public LocalEmbeddingService(OnnxLocalEmbeddingCapability? onnx)
    {
        _onnx = onnx;
    }

    public Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        return _onnx!.CreateEmbeddingAsync(text, cancellationToken);
    }

    public Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        return _onnx!.CreateBatchEmbeddingsAsync(texts, cancellationToken);
    }

    /// <summary>The vectors of the Knowledge Bases: the same free local model.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        return await _onnx!.CreateBatchEmbeddingsAsync(texts, cancellationToken).ConfigureAwait(false);
    }

    public int GetEmbeddingDimensions(ResourceBinding binding) => _onnx?.EmbeddingDimensions ?? 384;

    public string GetModelName(ResourceBinding binding) => _onnx?.ModelName ?? "all-MiniLM-L6-v2 (ONNX Local)";

    private void EnsureAvailable()
    {
        if (_onnx is null)
            throw new InvalidOperationException("The free local embedding model is not available: it could not be downloaded when the platform started (the log says why). Connect an Embeddings node to the memory, or restart the platform once it can reach huggingface.co.");
    }

    public void Dispose() => _onnx?.Dispose();
}
