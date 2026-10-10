using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Memory;

/// <summary>
/// Simple LLM Memory Node — 100% offline by default (the shared local ONNX model, no credentials
/// needed), but accepts an optional "embeddings" resource port to swap in a different provider
/// (e.g. <c>EmbeddingsOpenAiNode</c>) without touching anything else on this node.
/// </summary>
public sealed class LlmMemorySimpleNode : ResourceNode, IEmbeddingProvider
{
    private readonly LocalEmbeddingService _local;

    public LlmMemorySimpleNode(LocalEmbeddingService local)
        : base("ai.memory.simple")
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmMemory);

        DefineMetadata("Simple LLM Memory", "Enables RAG and vector clustering using a local ONNX model (no credentials needed).", "ai.memory");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Memory", ResourcePortCategory.LlmMemory, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "Optional — overrides the free local embedding with a connected Embeddings node.", isRequired: false);

        DefineChoice("mode", [("local_clustering", "Local Vector Clustering (K-means)"), ("embedding_only", "Embeddings Only (no clustering)")], "Memory mode", "local_clustering", ui: new ArgumentUiMetadata { Group = "Settings", Order = 10 });

        DefineInput(
            "max_clusters",
            ArgumentType.Number,
            "Maximum clusters for k-means",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Advanced",
                Order = 20,
                Min = 1,
                Step = 1,
            }
        );

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.CreateEmbeddingAsync(providerBinding, text, cancellationToken);
    }

    public Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.CreateBatchEmbeddingsAsync(providerBinding, texts, cancellationToken);
    }

    public int GetEmbeddingDimensions(ResourceBinding binding)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.GetEmbeddingDimensions(providerBinding);
    }

    public string GetModelName(ResourceBinding binding)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.GetModelName(providerBinding);
    }

    private (IEmbeddingProvider Provider, ResourceBinding Binding) Resolve(ResourceBinding binding) => EmbeddingProviderResolver.Resolve(binding, "embeddings", bundled: null, localFallback: _local);
}
