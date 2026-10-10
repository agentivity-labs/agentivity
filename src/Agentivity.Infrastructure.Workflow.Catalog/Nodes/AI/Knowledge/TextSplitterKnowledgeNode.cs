using System.Globalization;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

/// <summary>
/// Wraps an upstream <see cref="IKnowledgeSource"/>, splitting each of its documents into smaller
/// chunks before they reach a vector store — sized to stay well under an embedding model's input
/// limit and a retrieval chunk's usefulness sweet spot (a 50-page inline document embedded as one
/// vector loses almost all of its retrieval precision). A <see cref="ResourceNode"/> exposing
/// <see cref="ResourcePortCategory.KnowledgeSource"/> on BOTH ends — it slots into the exact same
/// "knowledge" port any other knowledge source uses, with the real source connected to its own
/// "source" input port instead of directly to the agent.
/// </summary>
public sealed class TextSplitterKnowledgeNode : ResourceNode, IKnowledgeSource
{
    public TextSplitterKnowledgeNode()
        : base("ai.knowledge.splitter")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("Text Splitter", "Splits an upstream knowledge source's documents into smaller chunks, sized for embedding/vector-store use.", "ai.knowledge");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineResourceInputPort("source", "Source", ResourcePortCategory.KnowledgeSource, "The knowledge source whose documents get split into chunks.");

        DefineChoice(
            "strategy",
            [("recursive_character", "Recursive Character (paragraphs → lines → sentences → words)"), ("token", "Approximate Token Count (word-based)")],
            "Splitting strategy",
            "recursive_character",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 10 }
        );
        DefineInput("chunk_size", ArgumentType.Number, "Maximum chunk size — characters for Recursive Character, approximate tokens (words) for Token", 1000.0, ui: new ArgumentUiMetadata { Group = "Settings", Order = 20, Min = 1 });
        DefineInput("chunk_overlap", ArgumentType.Number, "Overlap between consecutive chunks, same unit as chunk size", 150.0, ui: new ArgumentUiMetadata { Group = "Settings", Order = 30, Min = 0 });

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();

        (IKnowledgeSource source, ResourceBinding sourceBinding) = ResourceResolver.RequireToolNode<IKnowledgeSource>(context, "source", ResourcePortCategory.KnowledgeSource);
        IReadOnlyList<KnowledgeDocument> documents = await source.LoadAsync(context, sourceBinding, cancellationToken).ConfigureAwait(false);

        int chunkSize = binding.GetInt("chunk_size") ?? 1000;
        int chunkOverlap = binding.GetInt("chunk_overlap") ?? 150;
        TextSplitStrategy strategy = ParseStrategy(binding.GetString("strategy"));

        var result = new List<KnowledgeDocument>();
        foreach (KnowledgeDocument document in documents)
        {
            IReadOnlyList<string> chunks = TextChunker.Split(document.Text, chunkSize, chunkOverlap, strategy);
            for (var i = 0; i < chunks.Count; i++)
            {
                var metadata = document.Metadata is { Count: > 0 } ? new Dictionary<string, string>(document.Metadata, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
                metadata["chunk_index"] = i.ToString(CultureInfo.InvariantCulture);
                metadata["chunk_count"] = chunks.Count.ToString(CultureInfo.InvariantCulture);

                result.Add(new KnowledgeDocument { Text = chunks[i], Metadata = metadata });
            }
        }

        return result;
    }

    private static TextSplitStrategy ParseStrategy(string? value) => string.Equals(value, "token", StringComparison.OrdinalIgnoreCase) ? TextSplitStrategy.Token : TextSplitStrategy.RecursiveCharacter;
}
