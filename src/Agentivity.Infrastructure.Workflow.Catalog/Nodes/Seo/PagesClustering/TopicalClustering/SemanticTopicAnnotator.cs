using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Semantic topic annotator using LLM for topic extraction and embeddings for similarity.
/// Corresponds to TAGME annotation in the paper (Step 1).
///
/// For each page, extracts:
/// - A set of disambiguated topics (concepts)
/// - Weight ρ(page, topic) for each topic
/// </summary>
public sealed class SemanticTopicAnnotator
{
    private readonly ILogger? _logger;
    private readonly SemanticAnnotatorOptions _options;

    public SemanticTopicAnnotator(SemanticAnnotatorOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new SemanticAnnotatorOptions();
        _logger = logger;
    }

    /// <summary>
    /// Build the topical graph from pages using LLM topic extraction and embeddings.
    ///
    /// Step 1: Extract topics from each page using LLM
    /// Step 2: Create topic embeddings for similarity computation
    /// </summary>
    public async Task<TopicalGraph> BuildTopicalGraphAsync(List<SitemapPage> pages, Func<string, string?, CancellationToken, Task<TopicExtractionResult>> extractTopicsAsync, IEmbeddingCapability embeddingProvider, CancellationToken cancellationToken)
    {
        var graph = new TopicalGraph();
        var allTopics = new Dictionary<string, TopicInfo>(StringComparer.OrdinalIgnoreCase);

        _logger?.LogInformation("Step 1: Annotating {PageCount} pages with semantic topics...", pages.Count);

        // Step 1: Extract topics from each page
        int processedCount = 0;
        foreach (SitemapPage page in pages)
        {
            PageNode pageNode = graph.AddPage(page);

            try
            {
                // Build page text for topic extraction
                string pageText = BuildPageText(page);

                // Extract topics using LLM
                TopicExtractionResult result = await extractTopicsAsync(pageText, page.Url, cancellationToken);

                foreach (TopicWeight topicWeight in result.Topics)
                {
                    string topicId = NormalizeTopicId(topicWeight.Topic);

                    // Track all topics for embedding later
                    if (!allTopics.ContainsKey(topicId))
                    {
                        allTopics[topicId] = new TopicInfo { Id = topicId, Name = topicWeight.Topic };
                    }

                    // Add topic to graph if not exists
                    if (!graph.Topics.ContainsKey(topicId))
                    {
                        _ = graph.AddTopic(topicId, topicWeight.Topic);
                    }

                    // Add page-topic edge with weight ρ
                    graph.AddPageTopicEdge(page.Url, topicId, topicWeight.Weight);
                }

                processedCount++;
                if (processedCount % 10 == 0)
                {
                    _logger?.LogInformation("Annotated {Count}/{Total} pages...", processedCount, pages.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to extract topics from {Url}", page.Url);
            }
        }

        _logger?.LogInformation("Extracted {TopicCount} unique topics from {PageCount} pages", graph.Topics.Count, processedCount);

        // Step 2: Create embeddings for all topics
        _logger?.LogInformation("Step 2: Creating embeddings for {TopicCount} topics...", graph.Topics.Count);

        var topicList = graph.Topics.Values.ToList();
        int embeddingCount = 0;

        foreach (TopicNode? topic in topicList)
        {
            try
            {
                topic.Embedding = await embeddingProvider.CreateEmbeddingAsync(topic.Name, cancellationToken);
                embeddingCount++;

                if (embeddingCount % 20 == 0)
                {
                    _logger?.LogInformation("Created {Count}/{Total} topic embeddings...", embeddingCount, topicList.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create embedding for topic {Topic}", topic.Name);
            }
        }

        // Step 3: Compute topic-topic similarities
        _logger?.LogInformation("Step 3: Computing topic-topic similarities...");
        ComputeTopicSimilarities(graph);

        return graph;
    }

    /// <summary>
    /// Compute pairwise similarities between all topics using embeddings.
    /// </summary>
    private void ComputeTopicSimilarities(TopicalGraph graph)
    {
        var topics = graph.Topics.Values.Where(t => t.Embedding != null).ToList();
        int computed = 0;
        int total = topics.Count * (topics.Count - 1) / 2;

        for (int i = 0; i < topics.Count; i++)
        {
            for (int j = i + 1; j < topics.Count; j++)
            {
                float sim = CosineSimilarity(topics[i].Embedding!, topics[j].Embedding!);

                // Only store significant similarities
                if (sim > _options.MinSimilarityThreshold)
                {
                    graph.SetTopicSimilarity(topics[i].Id, topics[j].Id, sim);
                }

                computed++;
            }
        }

        int storedEdges = graph.TopicSimilarities.Count;
        _logger?.LogInformation("Computed {Computed} topic similarities, stored {Stored} significant edges (>{Threshold:P0})", computed, storedEdges, _options.MinSimilarityThreshold);
    }

    private string BuildPageText(SitemapPage page)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(page.Title))
            parts.Add($"Title: {page.Title}");

        if (!string.IsNullOrEmpty(page.H1))
            parts.Add($"H1: {page.H1}");

        if (!string.IsNullOrEmpty(page.MetaDescription))
            parts.Add($"Description: {page.MetaDescription}");

        // Extract semantic hints from URL
        try
        {
            string path = new Uri(page.Url).AbsolutePath.Trim('/');
            string urlTopics = path.Replace('/', ' ').Replace('-', ' ').Replace('_', ' ');
            if (!string.IsNullOrEmpty(urlTopics))
                parts.Add($"URL topics: {urlTopics}");
        }
        catch { }

        if (!string.IsNullOrEmpty(page.Excerpt))
        {
            string excerpt = page.Excerpt.Length > 500 ? page.Excerpt[..500] : page.Excerpt;
            parts.Add($"Content: {excerpt}");
        }

        return string.Join("\n", parts);
    }

    private static string NormalizeTopicId(string topic)
    {
        return topic.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        float dotProduct = 0,
            normA = 0,
            normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 1e-10f ? dotProduct / denom : 0;
    }

    private class TopicInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}

/// <summary>
/// Options for semantic annotator
/// </summary>
public sealed class SemanticAnnotatorOptions
{
    /// <summary>Minimum similarity to store as edge</summary>
    public double MinSimilarityThreshold { get; set; } = 0.3;

    /// <summary>Maximum topics to extract per page</summary>
    public int MaxTopicsPerPage { get; set; } = 10;
}

/// <summary>
/// Result of topic extraction from a page
/// </summary>
public sealed class TopicExtractionResult
{
    public List<TopicWeight> Topics { get; set; } = [];
}

/// <summary>
/// A topic with its weight ρ(page, topic)
/// </summary>
public sealed class TopicWeight
{
    public string Topic { get; set; } = string.Empty;
    public double Weight { get; set; }
}
