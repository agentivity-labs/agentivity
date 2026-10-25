using Agentivity.Infrastructure.Workflow.Catalog.Seo;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Page classifier implementing Steps 6-8 of the paper:
/// - Project topic clusters onto pages
/// - Identify pillar pages for each cluster
/// - Classify pages as pillar/cluster/leaf/orphan
/// </summary>
public sealed class PageClassifier
{
    private readonly PageClassifierOptions _options;

    public PageClassifier(PageClassifierOptions? options = null)
    {
        _options = options ?? new PageClassifierOptions();
    }

    /// <summary>
    /// Step 6: Project topic clusters onto pages.
    /// A page belongs to cluster Tᵢ if it has at least one topic in Tᵢ.
    /// Multiple memberships are allowed.
    /// </summary>
    public void ProjectClustersToPages(TopicalGraph graph, List<TopicClusterResult> topicClusters)
    {
        // Map topic ID -> cluster IDs
        var topicToCluster = new Dictionary<string, List<int>>();

        foreach (TopicClusterResult cluster in topicClusters)
        {
            foreach (string topicId in cluster.TopicIds)
            {
                if (!topicToCluster.ContainsKey(topicId))
                    topicToCluster[topicId] = [];
                topicToCluster[topicId].Add(cluster.ClusterId);

                // Also update the topic node
                if (graph.Topics.TryGetValue(topicId, out TopicNode? topic))
                {
                    topic.ClusterId = cluster.ClusterId;
                }
            }
        }

        // Assign pages to clusters based on their topics
        foreach (PageNode page in graph.Pages.Values)
        {
            page.ClusterIds.Clear();

            foreach (string topicId in page.TopicWeights.Keys)
            {
                if (topicToCluster.TryGetValue(topicId, out List<int>? clusterIds))
                {
                    foreach (int clusterId in clusterIds)
                    {
                        if (!page.ClusterIds.Contains(clusterId))
                            page.ClusterIds.Add(clusterId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Step 7: Identify pillar pages for each cluster.
    /// The pillar is the page p that maximizes h(Tᵢ) = Σ ρ(p, t) for t ∈ Tᵢ
    /// </summary>
    public Dictionary<int, string> IdentifyPillarPages(TopicalGraph graph, List<TopicClusterResult> topicClusters)
    {
        var clusterPillars = new Dictionary<int, string>();

        foreach (TopicClusterResult cluster in topicClusters)
        {
            var topicSet = cluster.TopicIds.ToHashSet();

            // Find pages that belong to this cluster
            var clusterPages = graph.Pages.Values.Where(p => p.ClusterIds.Contains(cluster.ClusterId)).ToList();

            if (clusterPages.Count == 0)
                continue;

            // Compute h(Tᵢ) score for each page
            foreach (PageNode? page in clusterPages)
            {
                double score = page.TopicWeights.Where(kv => topicSet.Contains(kv.Key)).Sum(kv => kv.Value);

                page.PillarScores[cluster.ClusterId] = score;
            }

            // The pillar is the page with maximum h(Tᵢ) score
            PageNode pillarPage = clusterPages.OrderByDescending(p => p.PillarScores.GetValueOrDefault(cluster.ClusterId, 0)).First();

            clusterPillars[cluster.ClusterId] = pillarPage.Url;
        }

        return clusterPillars;
    }

    /// <summary>
    /// Step 8: Classify all pages based on algorithmic criteria.
    /// - Pillar: max score in cluster, high coverage, high centrality
    /// - Cluster: strong cluster membership, partial coverage
    /// - Leaf: specialized topics, low coverage, few connections
    /// - Orphan: low ρ, weak cluster attachment, low connectivity
    /// </summary>
    public void ClassifyPages(TopicalGraph graph, List<TopicClusterResult> topicClusters, Dictionary<int, string> clusterPillars)
    {
        // Compute statistics for thresholds
        var allTopicWeights = graph.Pages.Values.Select(p => p.TotalTopicWeight).ToList();
        double avgTopicWeight = allTopicWeights.Count > 0 ? allTopicWeights.Average() : 0;
        double medianTopicWeight = GetMedian(allTopicWeights);

        var allClusterCounts = graph.Pages.Values.Select(p => p.ClusterIds.Count).ToList();
        double avgClusterCount = allClusterCounts.Count > 0 ? allClusterCounts.Average() : 0;

        foreach (PageNode page in graph.Pages.Values)
        {
            // Check if this page is a pillar
            bool isPillar = clusterPillars.Values.Contains(page.Url, StringComparer.OrdinalIgnoreCase);

            if (isPillar)
            {
                page.Classification = PageClassification.Pillar;
                continue;
            }

            // Compute classification metrics
            double topicCoverage = page.TotalTopicWeight;
            int clusterMembership = page.ClusterIds.Count;
            int connectionCount = page.SitemapPage.InternalLinkCount + page.SitemapPage.InboundLinkCount;

            // Classification logic based on paper criteria
            if (clusterMembership == 0 && topicCoverage < medianTopicWeight * _options.OrphanThreshold)
            {
                // No cluster attachment, low topic weight
                page.Classification = PageClassification.Orphan;
            }
            else if (topicCoverage < medianTopicWeight * _options.LeafThreshold && page.TopicWeights.Count <= 2)
            {
                // Low coverage, specialized (few topics)
                page.Classification = PageClassification.Leaf;
            }
            else if (clusterMembership > 0 && topicCoverage >= avgTopicWeight * _options.ClusterThreshold)
            {
                // Strong cluster membership, decent coverage
                page.Classification = PageClassification.Cluster;
            }
            else if (clusterMembership > 0)
            {
                // Has cluster membership but not strong enough for Cluster
                page.Classification = PageClassification.Leaf;
            }
            else
            {
                // Default to orphan for truly isolated pages
                page.Classification = PageClassification.Orphan;
            }
        }
    }

    /// <summary>
    /// Build the final site structure from classified pages.
    /// </summary>
    public SiteStructure BuildSiteStructure(TopicalGraph graph, List<TopicClusterResult> topicClusters, Dictionary<int, string> clusterPillars, string siteUrl)
    {
        var structure = new SiteStructure
        {
            SiteUrl = siteUrl,
            AnalyzedAt = DateTime.UtcNow,
            TotalPages = graph.Pages.Count,
            Pillars = [],
            TopicClusters = [],
            OrphanPages = [],
        };

        // Build ContentPillar for each cluster that has a pillar page
        foreach (TopicClusterResult cluster in topicClusters)
        {
            if (!clusterPillars.TryGetValue(cluster.ClusterId, out string? pillarUrl))
                continue;

            if (!graph.Pages.TryGetValue(pillarUrl, out PageNode? pillarPage))
                continue;

            var clusterPages = graph.Pages.Values.Where(p => p.ClusterIds.Contains(cluster.ClusterId)).Where(p => !p.Url.Equals(pillarUrl, StringComparison.OrdinalIgnoreCase)).ToList();

            // Separate leaf pages and potential sub-pillars
            var leafPages = clusterPages.Where(p => p.Classification == PageClassification.Leaf || p.Classification == PageClassification.Cluster).Select(p => p.SitemapPage).ToList();

            // Get topic names for this cluster
            var topicNames = cluster.TopicIds.Select(id => graph.Topics.TryGetValue(id, out TopicNode? t) ? t.Name : null).Where(n => n != null).Cast<string>().ToList();

            var pillar = new ContentPillar
            {
                PillarPage = pillarPage.SitemapPage,
                LeafPages = leafPages,
                SubPillars = [], // Could be extended for hierarchical clusters
                SeoTitle = pillarPage.SitemapPage.Title ?? pillarUrl,
                SeoDescription = pillarPage.SitemapPage.MetaDescription,
                Keywords = topicNames.Take(7).ToList(),
                Level = 0,
            };

            structure.Pillars.Add(pillar);
        }

        // Collect orphan pages
        structure.OrphanPages = graph.Pages.Values.Where(p => p.Classification == PageClassification.Orphan).Select(p => p.SitemapPage).ToList();

        // Pages without clusters but not classified as orphans go to TopicClusters
        // (opportunity clusters without dominant pillar)
        var uncategorizedPages = graph.Pages.Values.Where(p => p.ClusterIds.Count == 0 && p.Classification != PageClassification.Orphan).Select(p => p.SitemapPage).ToList();

        if (uncategorizedPages.Count >= 3)
        {
            structure.TopicClusters.Add(
                new TopicCluster
                {
                    Topic = "Uncategorized Content",
                    Description = "Pages that didn't fit into any topic cluster - potential opportunity for new pillars",
                    Pages = uncategorizedPages,
                    Keywords = [],
                }
            );
        }

        return structure;
    }

    private static double GetMedian(List<double> values)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;

        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}

/// <summary>
/// Options for page classification
/// </summary>
public sealed class PageClassifierOptions
{
    /// <summary>Threshold multiplier for orphan classification (below median * this = orphan)</summary>
    public double OrphanThreshold { get; set; } = 0.3;

    /// <summary>Threshold multiplier for leaf classification</summary>
    public double LeafThreshold { get; set; } = 0.6;

    /// <summary>Threshold multiplier for cluster classification</summary>
    public double ClusterThreshold { get; set; } = 0.5;
}
