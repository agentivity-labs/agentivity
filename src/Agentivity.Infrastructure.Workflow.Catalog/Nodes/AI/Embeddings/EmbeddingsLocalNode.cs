using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Embeddings;

/// <summary>
/// Standalone local Embeddings node — the zero-configuration default: a shared ONNX model
/// (all-MiniLM-L6-v2), no credentials, no external API calls, no cost. Exposes
/// <see cref="ResourcePortCategory.Embeddings"/> so it can be plugged into any memory/vector-store
/// node's optional "embeddings" input port when that node's own bundled default isn't wanted.
/// <para>
/// Most users never need to place this node at all — every vector store already falls back to the
/// exact same shared <see cref="LocalEmbeddingService"/> when nothing is connected to its
/// "embeddings" port and it has no bundled credentials configured. This node exists for the one
/// case that needs it: making the choice explicit and visible on the canvas, or reusing the same
/// local model across several vector stores without repeating anything.
/// </para>
/// </summary>
public sealed class EmbeddingsLocalNode : ResourceNode, IEmbeddingProvider
{
    private readonly LocalEmbeddingService _local;

    public EmbeddingsLocalNode(LocalEmbeddingService local)
        : base("ai.embeddings.local")
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.Embeddings);

        DefineMetadata("Local Embeddings", "Free, offline embeddings (all-MiniLM-L6-v2) — no credentials, no external API calls.", "ai.embeddings", whenToUse: "To compare texts by meaning for free, offline: plug it into the Embeddings port of a vector store or of an SEO step (Sitemap Structure, Internal Link Suggester). Vector stores already use this model when nothing else is set.");
        DefineEffects(NodeEffects.None);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Embeddings", ResourcePortCategory.Embeddings, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default) => _local.CreateEmbeddingAsync(binding, text, cancellationToken);

    public Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default) => _local.CreateBatchEmbeddingsAsync(binding, texts, cancellationToken);

    public int GetEmbeddingDimensions(ResourceBinding binding) => _local.GetEmbeddingDimensions(binding);

    public string GetModelName(ResourceBinding binding) => _local.GetModelName(binding);
}
