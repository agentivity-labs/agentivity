using System.Security.Cryptography;
using System.Text;
using Agentivity.Infrastructure.Core.Llm.Knowledge;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Shared vector-store retrieval path — every RAG consumer (the workflow-node path, the standalone
/// agent path, and <c>ai.llm.prompt</c>'s own RAG helper) calls this instead of hand-rolling the
/// same embed+upsert+query sequence. Upserts every resolved knowledge document (idempotent — the
/// point id is a content hash, so re-running with unchanged knowledge does not create duplicates)
/// then queries the store's own ANN index for the top matches.
/// <para>
/// Skips re-embedding documents whose content hash already exists in the store (via
/// <see cref="IVectorStoreProvider.GetExistingIdsAsync"/>) — a document only gets embedded again
/// once its text actually changes, which is the id itself changing.
/// </para>
/// </summary>
public static class AgentMemoryVectorStoreRetrieval
{
    public static async ValueTask<string> RetrieveAsync(IVectorStoreProvider vectorStore, IEmbeddingProvider embeddingProvider, ResourceBinding binding, IReadOnlyList<KnowledgeDocument> knowledge, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        var ids = knowledge.Select(document => ContentHashId(document.Text)).ToList();
        var existingIds = await vectorStore.GetExistingIdsAsync(binding, ids, cancellationToken).ConfigureAwait(false);

        var toEmbedIndexes = new List<int>();
        for (var index = 0; index < knowledge.Count; index++)
        {
            if (!existingIds.Contains(ids[index]))
            {
                toEmbedIndexes.Add(index);
            }
        }

        if (toEmbedIndexes.Count > 0)
        {
            var texts = toEmbedIndexes.Select(index => knowledge[index].Text);
            var embeddings = await embeddingProvider.CreateBatchEmbeddingsAsync(binding, texts, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < toEmbedIndexes.Count; i++)
            {
                var document = knowledge[toEmbedIndexes[i]];
                await vectorStore.UpsertAsync(binding, ids[toEmbedIndexes[i]], embeddings[i], document.Text, document.Metadata, cancellationToken).ConfigureAwait(false);
            }
        }

        var matches = await vectorStore.QueryAsync(binding, queryEmbedding, topK, cancellationToken).ConfigureAwait(false);
        return FormatContext(matches);
    }

    private static string ContentHashId(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string FormatContext(IReadOnlyList<VectorMatch> matches)
    {
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            builder.AppendLine($"[Knowledge {index + 1}]");
            builder.AppendLine(match.Text);

            if (match.Metadata is { Count: > 0 })
            {
                builder.AppendLine($"Source: {string.Join(", ", match.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
