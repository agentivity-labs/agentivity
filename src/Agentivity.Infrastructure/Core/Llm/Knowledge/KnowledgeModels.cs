using System.Text;

namespace Agentivity.Infrastructure.Core.Llm.Knowledge;

public sealed record KnowledgeDocument
{
    public string Text { get; init; } = string.Empty;

    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed record KnowledgeMatch(KnowledgeDocument Document, float Distance);

public static class KnowledgeRetrieval
{
    public static IReadOnlyList<KnowledgeMatch> RankTopMatches(float[] queryEmbedding, IReadOnlyList<KnowledgeDocument> documents, IReadOnlyList<float[]> documentEmbeddings, int topK)
    {
        return documents.Zip(documentEmbeddings, (document, embedding) => new KnowledgeMatch(document, CosineDistance(queryEmbedding, embedding))).OrderBy(match => match.Distance).Take(topK).ToList();
    }

    public static string FormatContext(IReadOnlyList<KnowledgeMatch> matches, string sectionLabel)
    {
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            builder.AppendLine($"[{sectionLabel} {index + 1}]");
            builder.AppendLine(match.Document.Text);

            if (match.Document.Metadata is { Count: > 0 })
            {
                builder.AppendLine($"Source: {string.Join(", ", match.Document.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static float CosineDistance(float[] a, float[] b)
    {
        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (var index = 0; index < a.Length; index++)
        {
            dotProduct += a[index] * b[index];
            normA += a[index] * a[index];
            normB += b[index] * b[index];
        }

        if (normA <= 0 || normB <= 0)
        {
            return 1;
        }

        return 1 - (dotProduct / (float)(System.Math.Sqrt(normA) * System.Math.Sqrt(normB)));
    }
}
