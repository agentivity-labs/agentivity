using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Multi-layer graph for topical clustering.
///
/// Layers:
/// 1. Page → Topic edges (semantic)
/// 2. Topic ↔ Topic edges (semantic similarity)
/// 3. Page ↔ Page edges (internal links - CRITICAL for hub detection)
///
/// This addresses the problem of hub misclassification by considering
/// both semantic AND structural signals.
/// </summary>
public sealed class MultiLayerTopicalGraph
{
    private readonly ILogger _logger;
    private readonly MultiLayerGraphOptions _options;

    public Dictionary<string, PageGraphNode> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TopicGraphNode> Topics { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Edge storage
    public Dictionary<(string PageUrl, string TopicId), double> PageTopicEdges { get; } = [];
    public Dictionary<(string TopicA, string TopicB), double> TopicTopicEdges { get; } = [];
    public Dictionary<(string PageA, string PageB), double> PagePageEdges { get; } = [];

    public MultiLayerTopicalGraph(ILogger logger, MultiLayerGraphOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new MultiLayerGraphOptions();
    }

    /// <summary>
    /// Build the complete multi-layer graph from page data.
    /// </summary>
    public void BuildGraph(List<SitemapPage> pages, Dictionary<string, PageTopicResult> topicResults)
    {
        _logger.LogInformation("Building multi-layer graph from {PageCount} pages...", pages.Count);

        // Step 1: Add all page nodes
        foreach (SitemapPage page in pages)
        {
            var node = new PageGraphNode
            {
                Url = page.Url,
                SitemapPage = page,
                PageRank = page.PageRankScore,
                InDegree = page.InboundLinkCount,
                OutDegree = page.InternalLinkCount,
            };

            if (topicResults.TryGetValue(page.Url, out PageTopicResult? topicResult))
            {
                node.Embedding = topicResult.PageEmbedding;
            }

            Pages[page.Url] = node;
        }

        // Step 2: Add Page → Page edges (internal links)
        BuildPagePageEdges(pages);

        // Step 3: Add topics and Page → Topic edges
        foreach ((string? url, PageTopicResult? result) in topicResults)
        {
            if (!Pages.TryGetValue(url, out PageGraphNode? pageNode))
                continue;

            foreach (ExtractedTopic topic in result.Topics)
            {
                string topicId = NormalizeTopicId(topic.Concept);

                // Add topic if new
                if (!Topics.ContainsKey(topicId))
                {
                    Topics[topicId] = new TopicGraphNode
                    {
                        Id = topicId,
                        Name = topic.Concept,
                        Embedding = topic.Embedding,
                    };
                }

                // Add Page → Topic edge
                PageTopicEdges[(url, topicId)] = topic.Weight;
                pageNode.TopicWeights[topicId] = topic.Weight;
                Topics[topicId].PageWeights[url] = topic.Weight;
            }
        }

        // Step 4: Build Topic ↔ Topic similarity edges
        BuildTopicTopicEdges();

        _logger.LogInformation("Graph built: {Pages} pages, {Topics} topics, {PT} page-topic edges, {TT} topic-topic edges, {PP} page-page edges", Pages.Count, Topics.Count, PageTopicEdges.Count, TopicTopicEdges.Count, PagePageEdges.Count);
    }

    private void BuildPagePageEdges(List<SitemapPage> pages)
    {
        var pageUrls = pages.Select(p => p.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (SitemapPage page in pages)
        {
            if (page.InternalLinks == null)
                continue;

            foreach (string targetUrl in page.InternalLinks)
            {
                if (!pageUrls.Contains(targetUrl))
                    continue;
                if (targetUrl.Equals(page.Url, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Normalize to canonical order
                (string, string) key = string.Compare(page.Url, targetUrl, StringComparison.OrdinalIgnoreCase) < 0 ? (page.Url, targetUrl) : (targetUrl, page.Url);

                // Edge weight = 1 / sqrt(outdegree of source) for PageRank-like normalization
                double weight = 1.0 / Math.Sqrt(Math.Max(1, page.InternalLinkCount));

                if (PagePageEdges.TryGetValue(key, out double existing))
                {
                    PagePageEdges[key] = Math.Max(existing, weight); // Keep max if bidirectional
                }
                else
                {
                    PagePageEdges[key] = weight;
                }
            }
        }
    }

    private void BuildTopicTopicEdges()
    {
        var topicList = Topics.Values.Where(t => t.Embedding.Length > 0).ToList();

        for (int i = 0; i < topicList.Count; i++)
        {
            for (int j = i + 1; j < topicList.Count; j++)
            {
                float sim = CosineSimilarity(topicList[i].Embedding, topicList[j].Embedding);

                if (sim > _options.MinTopicSimilarity)
                {
                    (string, string) key = (topicList[i].Id, topicList[j].Id);
                    TopicTopicEdges[key] = sim;
                }
            }
        }
    }

    /// <summary>
    /// Apply weighted set-cover to reduce topic count while preserving coverage.
    /// </summary>
    public HashSet<string> ReduceTopics()
    {
        var selected = new HashSet<string>();
        var uncoveredPages = new HashSet<string>(Pages.Keys, StringComparer.OrdinalIgnoreCase);

        // Filter overly generic topics (cover > 80% of pages)
        int maxCoverage = (int)(Pages.Count * _options.MaxTopicCoverageRatio);
        var candidateTopics = Topics.Values.Where(t => t.PageWeights.Count <= maxCoverage).OrderByDescending(t => t.TotalWeight).ToList();

        while (uncoveredPages.Count > 0 && candidateTopics.Count > 0)
        {
            // Find topic that covers most uncovered pages with highest weight
            TopicGraphNode? best = null;
            double bestScore = 0;

            foreach (TopicGraphNode? topic in candidateTopics)
            {
                int newlyCovered = topic.PageWeights.Keys.Count(p => uncoveredPages.Contains(p));
                if (newlyCovered == 0)
                    continue;

                double uncoveredWeight = topic.PageWeights.Where(kv => uncoveredPages.Contains(kv.Key)).Sum(kv => kv.Value);

                double score = uncoveredWeight * Math.Log(1 + newlyCovered);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = topic;
                }
            }

            if (best == null)
                break;

            _ = selected.Add(best.Id);
            best.IsSelected = true;
            _ = candidateTopics.Remove(best);

            foreach (string pageUrl in best.PageWeights.Keys)
            {
                _ = uncoveredPages.Remove(pageUrl);
            }

            if (selected.Count >= _options.MaxTopicsAfterReduction)
                break;
        }

        // Mark unselected topics
        foreach (TopicGraphNode topic in Topics.Values)
        {
            topic.IsSelected = selected.Contains(topic.Id);
        }

        _logger.LogInformation("Topic reduction: {Selected}/{Total} topics selected, covering {Covered}/{Total} pages", selected.Count, Topics.Count, Pages.Count - uncoveredPages.Count, Pages.Count);

        return selected;
    }

    /// <summary>
    /// Get adjacency matrix for selected topics only.
    /// </summary>
    public TopicAdjacencyMatrix GetTopicAdjacencyMatrix()
    {
        var selectedTopics = Topics.Values.Where(t => t.IsSelected).OrderBy(t => t.Id).ToList();
        int n = selectedTopics.Count;
        double[,] matrix = new double[n, n];
        var indexMap = new Dictionary<string, int>();

        for (int i = 0; i < n; i++)
        {
            indexMap[selectedTopics[i].Id] = i;
        }

        // Fill adjacency matrix from Topic↔Topic edges
        foreach (((string? topicA, string? topicB), double weight) in TopicTopicEdges)
        {
            if (indexMap.TryGetValue(topicA, out int i) && indexMap.TryGetValue(topicB, out int j))
            {
                matrix[i, j] = weight;
                matrix[j, i] = weight;
            }
        }

        return new TopicAdjacencyMatrix
        {
            Topics = selectedTopics,
            IndexMap = indexMap,
            Matrix = matrix,
        };
    }

    /// <summary>
    /// Compute PageRank-like authority score using the Page↔Page graph.
    /// This is CRITICAL for distinguishing true pillars from popular articles.
    /// </summary>
    public void ComputeGraphAuthority()
    {
        const int iterations = 20;
        const double dampingFactor = 0.85;

        int n = Pages.Count;
        var pageList = Pages.Values.ToList();
        var urlToIndex = pageList.Select((p, i) => (p.Url, i)).ToDictionary(x => x.Url, x => x.i, StringComparer.OrdinalIgnoreCase);

        // Initialize scores
        double[] scores = new double[n];
        double[] newScores = new double[n];
        for (int i = 0; i < n; i++)
            scores[i] = 1.0 / n;

        // Build outgoing link count
        int[] outDegree = new int[n];
        foreach (PageGraphNode? page in pageList)
        {
            int idx = urlToIndex[page.Url];
            outDegree[idx] = page.SitemapPage?.InternalLinkCount ?? 0;
        }

        // PageRank iteration
        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Fill(newScores, (1 - dampingFactor) / n);

            foreach (((string? pageA, string? pageB), double weight) in PagePageEdges)
            {
                if (!urlToIndex.TryGetValue(pageA, out int idxA))
                    continue;
                if (!urlToIndex.TryGetValue(pageB, out int idxB))
                    continue;

                // Distribute score from A to B (and B to A for undirected)
                if (outDegree[idxA] > 0)
                    newScores[idxB] += dampingFactor * scores[idxA] / outDegree[idxA];
                if (outDegree[idxB] > 0)
                    newScores[idxA] += dampingFactor * scores[idxB] / outDegree[idxB];
            }

            Array.Copy(newScores, scores, n);
        }

        // Normalize to 0-100
        double maxScore = scores.Max();
        for (int i = 0; i < n; i++)
        {
            pageList[i].GraphAuthority = maxScore > 0 ? scores[i] / maxScore * 100 : 0;
        }
    }

    private static string NormalizeTopicId(string concept)
    {
        return concept.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
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
}

public sealed class PageGraphNode
{
    public string Url { get; set; } = "";
    public SitemapPage? SitemapPage { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public Dictionary<string, double> TopicWeights { get; set; } = [];
    public double PageRank { get; set; }
    public int InDegree { get; set; }
    public int OutDegree { get; set; }
    public double GraphAuthority { get; set; } // Computed from multi-layer graph
    public int ClusterId { get; set; } = -1;
    public double ClusterCentrality { get; set; }
    public PageRole Role { get; set; } = PageRole.Unknown;
}

public sealed class TopicGraphNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public Dictionary<string, double> PageWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsSelected { get; set; } = true;
    public int ClusterId { get; set; } = -1;
    public double TotalWeight => PageWeights.Values.Sum();
}

public enum PageRole
{
    Unknown,
    Pillar,
    Cluster,
    Leaf,
    Orphan,
}

public sealed class TopicAdjacencyMatrix
{
    public List<TopicGraphNode> Topics { get; set; } = [];
    public Dictionary<string, int> IndexMap { get; set; } = [];
    public double[,] Matrix { get; set; } = new double[0, 0];
    public int Size => Topics.Count;
}

public sealed class MultiLayerGraphOptions
{
    public double MinTopicSimilarity { get; set; } = 0.3;
    public double MaxTopicCoverageRatio { get; set; } = 0.8;
    public int MaxTopicsAfterReduction { get; set; } = 100;
}
