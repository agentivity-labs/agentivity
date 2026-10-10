using Agentivity.Infrastructure.Workflow.Catalog.Seo;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Represents a topic (concept) extracted from pages.
/// Corresponds to TAGME annotations in the paper.
/// </summary>
public sealed class TopicNode
{
    /// <summary>Unique identifier for this topic</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable topic name (disambiguated concept)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Topic embedding vector for similarity calculations</summary>
    public float[]? Embedding { get; set; }

    /// <summary>Set of page URLs that have this topic with their weights ρ(page, topic)</summary>
    public Dictionary<string, double> PageWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of pages containing this topic</summary>
    public int PageCount => PageWeights.Count;

    /// <summary>Total weight across all pages (sum of ρ values)</summary>
    public double TotalWeight => PageWeights.Values.Sum();

    /// <summary>Whether this topic was selected after set-cover reduction</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>Cluster assignment after spectral clustering (-1 = unassigned)</summary>
    public int ClusterId { get; set; } = -1;
}

/// <summary>
/// Represents the bipartite graph of pages and topics.
/// This is the core data structure for the topical clustering algorithm.
/// </summary>
public sealed class TopicalGraph
{
    /// <summary>All pages in the graph (P nodes)</summary>
    public Dictionary<string, PageNode> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All topics in the graph (T nodes)</summary>
    public Dictionary<string, TopicNode> Topics { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Topic-to-topic similarity matrix (sparse representation)</summary>
    public Dictionary<(string, string), double> TopicSimilarities { get; } = [];

    /// <summary>Page-to-page edges (internal links or high similarity)</summary>
    public Dictionary<(string, string), double> PageLinks { get; } = [];

    /// <summary>
    /// Add a page to the graph
    /// </summary>
    public PageNode AddPage(SitemapPage sitemapPage)
    {
        if (Pages.TryGetValue(sitemapPage.Url, out PageNode? existing))
            return existing;

        var node = new PageNode { Url = sitemapPage.Url, SitemapPage = sitemapPage };
        Pages[sitemapPage.Url] = node;
        return node;
    }

    /// <summary>
    /// Add a topic to the graph
    /// </summary>
    public TopicNode AddTopic(string topicId, string topicName, float[]? embedding = null)
    {
        if (Topics.TryGetValue(topicId, out TopicNode? existing))
            return existing;

        var node = new TopicNode
        {
            Id = topicId,
            Name = topicName,
            Embedding = embedding,
        };
        Topics[topicId] = node;
        return node;
    }

    /// <summary>
    /// Add an edge between a page and a topic with weight ρ(page, topic)
    /// </summary>
    public void AddPageTopicEdge(string pageUrl, string topicId, double weight)
    {
        if (!Pages.TryGetValue(pageUrl, out PageNode? page))
            throw new InvalidOperationException($"Page {pageUrl} not found in graph");

        if (!Topics.TryGetValue(topicId, out TopicNode? topic))
            throw new InvalidOperationException($"Topic {topicId} not found in graph");

        page.TopicWeights[topicId] = weight;
        topic.PageWeights[pageUrl] = weight;
    }

    /// <summary>
    /// Set similarity between two topics
    /// </summary>
    public void SetTopicSimilarity(string topicId1, string topicId2, double similarity)
    {
        if (topicId1 == topicId2)
            return;

        // Store in canonical order to avoid duplicates
        (string, string) key = string.Compare(topicId1, topicId2, StringComparison.Ordinal) < 0 ? (topicId1, topicId2) : (topicId2, topicId1);

        TopicSimilarities[key] = similarity;
    }

    /// <summary>
    /// Get similarity between two topics
    /// </summary>
    public double GetTopicSimilarity(string topicId1, string topicId2)
    {
        if (topicId1 == topicId2)
            return 1.0;

        (string, string) key = string.Compare(topicId1, topicId2, StringComparison.Ordinal) < 0 ? (topicId1, topicId2) : (topicId2, topicId1);

        return TopicSimilarities.TryGetValue(key, out double sim) ? sim : 0.0;
    }

    /// <summary>
    /// Get the reduced topic graph Gₜ (only selected topics)
    /// </summary>
    public TopicGraphMatrix GetReducedTopicGraph()
    {
        var selectedTopics = Topics.Values.Where(t => t.IsSelected).OrderBy(t => t.Id).ToList();

        int n = selectedTopics.Count;
        double[,] matrix = new double[n, n];
        var topicIndex = new Dictionary<string, int>();

        for (int i = 0; i < n; i++)
        {
            topicIndex[selectedTopics[i].Id] = i;
        }

        // Build adjacency matrix from similarities
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double sim = GetTopicSimilarity(selectedTopics[i].Id, selectedTopics[j].Id);
                matrix[i, j] = sim;
                matrix[j, i] = sim;
            }
        }

        return new TopicGraphMatrix
        {
            Topics = selectedTopics,
            TopicIndex = topicIndex,
            AdjacencyMatrix = matrix,
        };
    }
}

/// <summary>
/// Represents a page node in the topical graph
/// </summary>
public sealed class PageNode
{
    /// <summary>Page URL (unique identifier)</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Reference to the original sitemap page</summary>
    public SitemapPage SitemapPage { get; set; } = new();

    /// <summary>Topics associated with this page and their weights ρ(page, topic)</summary>
    public Dictionary<string, double> TopicWeights { get; set; } = [];

    /// <summary>Page classification after clustering</summary>
    public PageClassification Classification { get; set; } = PageClassification.Unknown;

    /// <summary>Cluster assignments (a page can belong to multiple clusters)</summary>
    public List<int> ClusterIds { get; set; } = [];

    /// <summary>Score for being the pillar of each cluster</summary>
    public Dictionary<int, double> PillarScores { get; set; } = [];

    /// <summary>Total weight across all topics</summary>
    public double TotalTopicWeight => TopicWeights.Values.Sum();
}

/// <summary>
/// Page classification according to the paper's methodology
/// </summary>
public enum PageClassification
{
    Unknown,
    Pillar, // Central page of a cluster (highest h(Tᵢ) score)
    Cluster, // Strong membership in a cluster, partial theme coverage
    Leaf, // Specialized topics, low coverage, few connections
    Orphan, // Low ρ, weak cluster attachment, low connectivity
}

/// <summary>
/// Matrix representation of the topic graph for spectral clustering
/// </summary>
public sealed class TopicGraphMatrix
{
    public List<TopicNode> Topics { get; set; } = [];
    public Dictionary<string, int> TopicIndex { get; set; } = [];
    public double[,] AdjacencyMatrix { get; set; } = new double[0, 0];

    public int Size => Topics.Count;
}
