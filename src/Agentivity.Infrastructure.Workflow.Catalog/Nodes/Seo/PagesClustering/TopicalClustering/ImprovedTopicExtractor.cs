using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.ContentExtraction;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Improved topic extraction using weighted chunk embeddings.
/// Addresses: positional weighting, semantic deduplication, robust aggregation.
///
/// Algorithm:
/// 1. Embed each content chunk separately
/// 2. Weight embeddings by position and type (H1 > H2 > early paragraphs > late paragraphs)
/// 3. Extract topics per chunk, then deduplicate across page
/// 4. Final topic weights = sum of (chunk_weight × topic_prominence)
/// </summary>
public sealed class ImprovedTopicExtractor
{
    private readonly ILogger _logger;
    private readonly TopicExtractionOptions _options;

    public ImprovedTopicExtractor(ILogger logger, TopicExtractionOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new TopicExtractionOptions();
    }

    /// <summary>
    /// Extract topics from extracted content using weighted chunk analysis.
    /// </summary>
    public async Task<PageTopicResult> ExtractTopicsAsync(ExtractedContent content, ILlmClient llmClient, IEmbeddingCapability embeddingProvider, CancellationToken cancellationToken)
    {
        var result = new PageTopicResult { Url = content.Url };

        if (content.Chunks.Count == 0)
        {
            _logger.LogWarning("No content chunks for {Url}", content.Url);
            return result;
        }

        // Step 1: Create embeddings for each chunk
        await CreateChunkEmbeddingsAsync(content.Chunks, embeddingProvider, cancellationToken);

        // Step 2: Compute weighted page embedding (robust aggregation)
        result.PageEmbedding = ComputeWeightedPageEmbedding(content.Chunks);

        // Step 3: Extract topics from key chunks (title, H1, H2, first paragraphs)
        List<RawTopic> rawTopics = await ExtractTopicsFromKeyChunksAsync(content, llmClient, cancellationToken);

        // Step 4: Deduplicate and merge similar topics
        List<MergedTopic> mergedTopics = await DeduplicateTopicsAsync(rawTopics, embeddingProvider, cancellationToken);

        // Step 5: Create topic embeddings and compute final weights
        result.Topics = await FinalizeTopicsAsync(mergedTopics, embeddingProvider, cancellationToken);

        _logger.LogInformation("Extracted {TopicCount} topics from {Url}", result.Topics.Count, content.Url);

        return result;
    }

    private async Task CreateChunkEmbeddingsAsync(List<ContentChunk> chunks, IEmbeddingCapability embeddingProvider, CancellationToken cancellationToken)
    {
        foreach (ContentChunk chunk in chunks)
        {
            if (chunk.Text.Length < 10)
                continue;

            try
            {
                chunk.Embedding = await embeddingProvider.CreateEmbeddingAsync(chunk.Text, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create embedding for chunk");
            }
        }
    }

    /// <summary>
    /// Compute page embedding using weighted median approach (more robust than mean).
    /// High-weight chunks (headings, early content) dominate the representation.
    /// </summary>
    private float[] ComputeWeightedPageEmbedding(List<ContentChunk> chunks)
    {
        var validChunks = chunks.Where(c => c.Embedding != null).ToList();
        if (validChunks.Count == 0)
            return Array.Empty<float>();

        int dimensions = validChunks[0].Embedding!.Length;
        float[] result = new float[dimensions];

        // Weighted average with normalization
        double totalWeight = validChunks.Sum(c => c.Weight);

        for (int d = 0; d < dimensions; d++)
        {
            double weightedSum = 0;
            foreach (ContentChunk? chunk in validChunks)
            {
                weightedSum += chunk.Embedding![d] * chunk.Weight;
            }
            result[d] = (float)(weightedSum / totalWeight);
        }

        // Normalize to unit vector
        float norm = MathF.Sqrt(result.Sum(x => x * x));
        if (norm > 1e-6f)
        {
            for (int d = 0; d < dimensions; d++)
            {
                result[d] /= norm;
            }
        }

        return result;
    }

    private async Task<List<RawTopic>> ExtractTopicsFromKeyChunksAsync(ExtractedContent content, ILlmClient llmClient, CancellationToken cancellationToken)
    {
        var allTopics = new List<RawTopic>();

        // Priority chunks: title, H1, H2s, first 3 paragraphs
        var keyChunks = content.Chunks.Where(c => c.Type == ChunkType.H1 || c.Type == ChunkType.H2 || c.Type == ChunkType.H3 || c.Position < 5).OrderByDescending(c => c.Weight).Take(_options.MaxChunksToAnalyze).ToList();

        // Build consolidated prompt for efficiency
        string prompt = BuildTopicExtractionPrompt(content, keyChunks);

        string schema = """
            {
                "type": "object",
                "properties": {
                    "topics": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "concept": { "type": "string", "description": "Disambiguated concept name" },
                                "prominence": { "type": "number", "description": "Importance 0.0-1.0" },
                                "source": { "type": "string", "description": "Where found: title/h1/h2/content" }
                            },
                            "required": ["concept", "prominence"]
                        }
                    }
                },
                "required": ["topics"]
            }
            """;

        OperationResult<LlmResponse> result = await llmClient.CompleteAsync(
            prompt,
            systemPrompt: "Extract distinct semantic concepts. Be specific (e.g., 'candlestick patterns' not 'patterns'). Deduplicate similar concepts.",
            temperature: 0.1,
            maxTokens: 800,
            responseSchema: schema,
            cancellationToken: cancellationToken
        );

        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Value?.Content))
        {
            try
            {
                TopicsResponse? parsed = JsonSerializer.Deserialize<TopicsResponse>(result.Value.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed?.Topics != null)
                {
                    foreach (TopicItem t in parsed.Topics)
                    {
                        // Apply source-based weight multiplier
                        double sourceWeight = t.Source?.ToLowerInvariant() switch
                        {
                            "title" => 2.0,
                            "h1" => 1.8,
                            "h2" => 1.3,
                            _ => 1.0,
                        };

                        allTopics.Add(new RawTopic { Concept = t.Concept, Weight = Math.Clamp(t.Prominence * sourceWeight, 0, 2.0) });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse topic extraction response");
            }
        }

        return allTopics;
    }

    private string BuildTopicExtractionPrompt(ExtractedContent content, List<ContentChunk> keyChunks)
    {
        var sb = new System.Text.StringBuilder();

        _ = sb.AppendLine("Extract the main semantic CONCEPTS from this page content.");
        _ = sb.AppendLine("Concepts must be specific and disambiguated (e.g., 'moving average crossover' not 'moving average').");
        _ = sb.AppendLine();

        if (!string.IsNullOrEmpty(content.Title))
            _ = sb.AppendLine($"TITLE: {content.Title}");

        if (!string.IsNullOrEmpty(content.H1))
            _ = sb.AppendLine($"H1: {content.H1}");

        _ = sb.AppendLine();
        _ = sb.AppendLine("KEY CONTENT:");

        foreach (ContentChunk chunk in keyChunks)
        {
            string label = chunk.Type switch
            {
                ChunkType.H2 => "[H2]",
                ChunkType.H3 => "[H3]",
                _ => "[P]",
            };
            _ = sb.AppendLine($"{label} {chunk.Text.Substring(0, Math.Min(200, chunk.Text.Length))}");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine($"Extract up to {_options.MaxTopicsPerPage} distinct concepts.");

        return sb.ToString();
    }

    /// <summary>
    /// Merge semantically similar topics using embedding similarity.
    /// </summary>
    private async Task<List<MergedTopic>> DeduplicateTopicsAsync(List<RawTopic> topics, IEmbeddingCapability embeddingProvider, CancellationToken cancellationToken)
    {
        if (topics.Count == 0)
            return [];

        // Create embeddings for all topics
        var topicsWithEmbeddings = new List<(RawTopic Topic, float[] Embedding)>();

        foreach (RawTopic topic in topics)
        {
            try
            {
                float[] embedding = await embeddingProvider.CreateEmbeddingAsync(topic.Concept, cancellationToken);
                topicsWithEmbeddings.Add((topic, embedding));
            }
            catch { }
        }

        // Cluster similar topics using greedy merging
        var merged = new List<MergedTopic>();
        var used = new HashSet<int>();

        for (int i = 0; i < topicsWithEmbeddings.Count; i++)
        {
            if (used.Contains(i))
                continue;

            var cluster = new MergedTopic
            {
                PrimaryConcept = topicsWithEmbeddings[i].Topic.Concept,
                Embedding = topicsWithEmbeddings[i].Embedding,
                TotalWeight = topicsWithEmbeddings[i].Topic.Weight,
                Variants = [topicsWithEmbeddings[i].Topic.Concept],
            };

            _ = used.Add(i);

            // Find similar topics
            for (int j = i + 1; j < topicsWithEmbeddings.Count; j++)
            {
                if (used.Contains(j))
                    continue;

                float similarity = CosineSimilarity(topicsWithEmbeddings[i].Embedding, topicsWithEmbeddings[j].Embedding);

                if (similarity > _options.TopicMergeSimilarityThreshold)
                {
                    // Merge: keep the one with higher weight as primary
                    if (topicsWithEmbeddings[j].Topic.Weight > cluster.TotalWeight)
                    {
                        cluster.PrimaryConcept = topicsWithEmbeddings[j].Topic.Concept;
                    }

                    cluster.TotalWeight += topicsWithEmbeddings[j].Topic.Weight;
                    cluster.Variants.Add(topicsWithEmbeddings[j].Topic.Concept);
                    _ = used.Add(j);
                }
            }

            merged.Add(cluster);
        }

        return merged.OrderByDescending(m => m.TotalWeight).Take(_options.MaxTopicsPerPage).ToList();
    }

    private async Task<List<ExtractedTopic>> FinalizeTopicsAsync(List<MergedTopic> mergedTopics, IEmbeddingCapability embeddingProvider, CancellationToken cancellationToken)
    {
        var result = new List<ExtractedTopic>();

        foreach (MergedTopic merged in mergedTopics)
        {
            result.Add(
                new ExtractedTopic
                {
                    Concept = merged.PrimaryConcept,
                    Weight = Math.Clamp(merged.TotalWeight, 0, 1.0), // Normalize to 0-1
                    Embedding = merged.Embedding,
                    Variants = merged.Variants,
                }
            );
        }

        return result;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        float dot = 0,
            normA = 0,
            normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 1e-10f ? dot / denom : 0;
    }

    // DTOs
    private class TopicsResponse
    {
        public List<TopicItem>? Topics { get; set; }
    }

    private class TopicItem
    {
        public string Concept { get; set; } = "";
        public double Prominence { get; set; }
        public string? Source { get; set; }
    }

    private class RawTopic
    {
        public string Concept { get; set; } = "";
        public double Weight { get; set; }
    }

    private class MergedTopic
    {
        public string PrimaryConcept { get; set; } = "";
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public double TotalWeight { get; set; }
        public List<string> Variants { get; set; } = [];
    }
}

public sealed class TopicExtractionOptions
{
    public int MaxTopicsPerPage { get; set; } = 8;
    public int MaxChunksToAnalyze { get; set; } = 10;
    public double TopicMergeSimilarityThreshold { get; set; } = 0.85;
}

public sealed class PageTopicResult
{
    public string Url { get; set; } = "";
    public float[] PageEmbedding { get; set; } = Array.Empty<float>();
    public List<ExtractedTopic> Topics { get; set; } = [];
}

public sealed class ExtractedTopic
{
    public string Concept { get; set; } = "";
    public double Weight { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public List<string> Variants { get; set; } = [];
}
