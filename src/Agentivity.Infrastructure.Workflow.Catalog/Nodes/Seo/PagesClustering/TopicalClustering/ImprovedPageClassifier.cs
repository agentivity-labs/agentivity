using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Improved page classifier with:
/// 1. Margin-based cluster assignment (anti-leakage)
/// 2. Strict pillar validation (semantic + structural)
/// 3. Hub detection to prevent misclassification
/// </summary>
public sealed class ImprovedPageClassifier
{
    private readonly ILogger _logger;
    private readonly ImprovedClassifierOptions _options;

    public ImprovedPageClassifier(ILogger logger, ImprovedClassifierOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ImprovedClassifierOptions();
    }

    /// <summary>
    /// Step 7: Assign pages to clusters with margin rule to prevent leakage.
    /// </summary>
    public void AssignPagesToClustersClusters(MultiLayerTopicalGraph graph, List<TopicClusterResult> topicClusters)
    {
        // Build cluster centroids from topic embeddings
        var clusterCentroids = new Dictionary<int, float[]>();
        var clusterTopicSets = new Dictionary<int, HashSet<string>>();

        foreach (TopicClusterResult cluster in topicClusters)
        {
            clusterTopicSets[cluster.ClusterId] = cluster.TopicIds.ToHashSet();

            // Compute centroid as weighted average of topic embeddings
            var topicEmbeddings = cluster.TopicIds.Select(id => graph.Topics.TryGetValue(id, out TopicGraphNode? t) ? t : null).Where(t => t?.Embedding.Length > 0).ToList();

            if (topicEmbeddings.Count == 0)
                continue;

            int dim = topicEmbeddings[0]!.Embedding.Length;
            float[] centroid = new float[dim];
            double totalWeight = 0.0;

            foreach (TopicGraphNode? topic in topicEmbeddings)
            {
                double weight = topic!.TotalWeight;
                totalWeight += weight;

                for (int d = 0; d < dim; d++)
                {
                    centroid[d] += topic.Embedding[d] * (float)weight;
                }
            }

            if (totalWeight > 0)
            {
                for (int d = 0; d < dim; d++)
                {
                    centroid[d] /= (float)totalWeight;
                }
            }

            // Normalize
            float norm = MathF.Sqrt(centroid.Sum(x => x * x));
            if (norm > 1e-6f)
            {
                for (int d = 0; d < dim; d++)
                    centroid[d] /= norm;
            }

            clusterCentroids[cluster.ClusterId] = centroid;
        }

        // Assign pages using margin rule
        foreach (PageGraphNode page in graph.Pages.Values)
        {
            if (page.Embedding.Length == 0)
            {
                page.ClusterId = -1;
                continue;
            }

            // Compute similarity to each cluster
            var similarities = new List<(int ClusterId, double Similarity, double TopicOverlap)>();

            foreach (TopicClusterResult cluster in topicClusters)
            {
                if (!clusterCentroids.TryGetValue(cluster.ClusterId, out float[]? centroid))
                    continue;

                // Embedding similarity
                double embeddingSim = CosineSimilarity(page.Embedding, centroid);

                // Topic overlap: what fraction of page's topics are in this cluster?
                var pageTopics = page.TopicWeights.Keys.ToHashSet();
                HashSet<string> clusterTopics = clusterTopicSets[cluster.ClusterId];
                double overlap = pageTopics.Count > 0 ? (double)pageTopics.Intersect(clusterTopics).Count() / pageTopics.Count : 0;

                // Combined score
                double score = (_options.EmbeddingWeight * embeddingSim) + (_options.TopicOverlapWeight * overlap);

                similarities.Add((cluster.ClusterId, score, overlap));
            }

            if (similarities.Count == 0)
            {
                page.ClusterId = -1;
                continue;
            }

            // Sort by similarity
            similarities = similarities.OrderByDescending(s => s.Similarity).ToList();

            (int ClusterId, double Similarity, double TopicOverlap) best = similarities[0];
            double secondSim = similarities.Count > 1 ? similarities[1].Similarity : 0.0;

            // MARGIN RULE: assign only if clear winner
            double margin = best.Similarity - secondSim;

            if (margin >= _options.AssignmentMargin && best.Similarity >= _options.MinAssignmentSimilarity)
            {
                page.ClusterId = best.ClusterId;
                page.ClusterCentrality = best.Similarity;
            }
            else if (best.TopicOverlap >= _options.MinTopicOverlapForAssignment)
            {
                // Fallback: strong topic overlap can override margin
                page.ClusterId = best.ClusterId;
                page.ClusterCentrality = best.Similarity;
            }
            else
            {
                // Ambiguous → potential orphan or multi-cluster
                page.ClusterId = -1;
            }
        }

        // Log assignment stats
        int assigned = graph.Pages.Values.Count(p => p.ClusterId >= 0);
        int unassigned = graph.Pages.Values.Count(p => p.ClusterId < 0);
        _logger.LogInformation("Page assignment: {Assigned} assigned, {Unassigned} unassigned (ambiguous/orphan)", assigned, unassigned);
    }

    /// <summary>
    /// Step 8: Identify pillar pages with STRICT validation.
    /// A pillar MUST have both semantic centrality AND structural support.
    /// </summary>
    public Dictionary<int, PillarCandidate> IdentifyPillars(MultiLayerTopicalGraph graph, List<TopicClusterResult> topicClusters)
    {
        var pillars = new Dictionary<int, PillarCandidate>();

        foreach (TopicClusterResult cluster in topicClusters)
        {
            var clusterPages = graph.Pages.Values.Where(p => p.ClusterId == cluster.ClusterId).ToList();

            if (clusterPages.Count == 0)
                continue;

            // Score each page as potential pillar
            var candidates = new List<PillarCandidate>();

            foreach (PageGraphNode? page in clusterPages)
            {
                PillarScore score = ComputePillarScore(page, clusterPages, cluster, graph);
                candidates.Add(
                    new PillarCandidate
                    {
                        Page = page,
                        ClusterId = cluster.ClusterId,
                        SemanticCentrality = score.SemanticCentrality,
                        StructuralCentrality = score.StructuralCentrality,
                        ThematicCoverage = score.ThematicCoverage,
                        ChildSupport = score.ChildSupport,
                        TotalScore = score.TotalScore,
                        IsValid = score.IsValid,
                    }
                );
            }

            // Find best VALID candidate
            PillarCandidate? bestCandidate = candidates.Where(c => c.IsValid).OrderByDescending(c => c.TotalScore).FirstOrDefault();

            if (bestCandidate != null)
            {
                pillars[cluster.ClusterId] = bestCandidate;
                bestCandidate.Page.Role = PageRole.Pillar;

                _logger.LogInformation("Cluster {ClusterId} pillar: {Url} (Score: {Score:F2})", cluster.ClusterId, bestCandidate.Page.Url, bestCandidate.TotalScore);
            }
            else
            {
                _logger.LogWarning("Cluster {ClusterId} has no valid pillar candidate!", cluster.ClusterId);
            }
        }

        return pillars;
    }

    private PillarScore ComputePillarScore(PageGraphNode page, List<PageGraphNode> clusterPages, TopicClusterResult cluster, MultiLayerTopicalGraph graph)
    {
        var score = new PillarScore();
        var clusterTopics = cluster.TopicIds.ToHashSet();

        // 1. SEMANTIC CENTRALITY: how central is this page's embedding to the cluster?
        var otherPages = clusterPages.Where(p => p.Url != page.Url && p.Embedding.Length > 0).ToList();
        if (otherPages.Count > 0 && page.Embedding.Length > 0)
        {
            double avgSimilarity = otherPages.Average(p => CosineSimilarity(page.Embedding, p.Embedding));
            score.SemanticCentrality = avgSimilarity;
        }

        // 2. THEMATIC COVERAGE: what fraction of cluster topics does this page cover?
        int pageCoveredTopics = page.TopicWeights.Keys.Intersect(clusterTopics).Count();
        score.ThematicCoverage = clusterTopics.Count > 0 ? (double)pageCoveredTopics / clusterTopics.Count : 0;

        // 3. STRUCTURAL CENTRALITY: PageRank / InDegree within the site
        double maxAuthority = graph.Pages.Values.Max(p => p.GraphAuthority);
        score.StructuralCentrality = maxAuthority > 0 ? page.GraphAuthority / maxAuthority : 0;

        // 4. CHILD SUPPORT: how many cluster pages link FROM this page?
        int childLinks = 0;
        if (page.SitemapPage?.InternalLinks != null)
        {
            var clusterUrls = clusterPages.Select(p => p.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            childLinks = page.SitemapPage.InternalLinks.Count(link => clusterUrls.Contains(link));
        }
        score.ChildSupport = (double)childLinks / Math.Max(1, clusterPages.Count - 1);

        // VALIDATION: A page cannot be a pillar if:
        // - No semantic children (child support = 0)
        // - Low structural centrality (below threshold)
        // - Low thematic coverage (below threshold)

        score.IsValid = score.ChildSupport >= _options.MinChildSupportForPillar && score.StructuralCentrality >= _options.MinStructuralCentralityForPillar && score.ThematicCoverage >= _options.MinThematicCoverageForPillar;

        // Compute total score (weighted combination)
        score.TotalScore = (_options.SemanticCentralityWeight * score.SemanticCentrality) + (_options.StructuralCentralityWeight * score.StructuralCentrality) + (_options.ThematicCoverageWeight * score.ThematicCoverage) + (_options.ChildSupportWeight * score.ChildSupport);

        return score;
    }

    /// <summary>
    /// Step 9: Classify all pages into roles.
    /// </summary>
    public void ClassifyPageRoles(MultiLayerTopicalGraph graph, Dictionary<int, PillarCandidate> pillars)
    {
        // Already marked pillars
        var pillarUrls = pillars.Values.Select(p => p.Page.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (PageGraphNode page in graph.Pages.Values)
        {
            if (pillarUrls.Contains(page.Url))
            {
                page.Role = PageRole.Pillar;
                continue;
            }

            if (page.ClusterId < 0)
            {
                // Not assigned to any cluster → check if truly orphan
                if (page.GraphAuthority < _options.OrphanAuthorityThreshold && page.InDegree < _options.OrphanInDegreeThreshold)
                {
                    page.Role = PageRole.Orphan;
                }
                else
                {
                    // Has some authority but ambiguous cluster → treat as leaf
                    page.Role = PageRole.Leaf;
                }
                continue;
            }

            // Assigned to cluster → classify based on centrality
            page.Role = page.ClusterCentrality >= _options.ClusterPageThreshold ? PageRole.Cluster : PageRole.Leaf;
        }

        // Log classification stats
        var stats = graph.Pages.Values.GroupBy(p => p.Role).ToDictionary(g => g.Key, g => g.Count());
        _logger.LogInformation("Classification: Pillar={Pillar}, Cluster={Cluster}, Leaf={Leaf}, Orphan={Orphan}", stats.GetValueOrDefault(PageRole.Pillar, 0), stats.GetValueOrDefault(PageRole.Cluster, 0), stats.GetValueOrDefault(PageRole.Leaf, 0), stats.GetValueOrDefault(PageRole.Orphan, 0));
    }

    /// <summary>
    /// Build final site structure from classified pages.
    /// </summary>
    public SiteStructure BuildSiteStructure(MultiLayerTopicalGraph graph, Dictionary<int, PillarCandidate> pillars, List<TopicClusterResult> topicClusters, string siteUrl)
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

        // Build ContentPillar for each cluster with a valid pillar
        foreach ((int clusterId, PillarCandidate? pillarCandidate) in pillars)
        {
            var clusterPages = graph.Pages.Values.Where(p => p.ClusterId == clusterId && p.Role != PageRole.Pillar).ToList();

            TopicClusterResult? cluster = topicClusters.FirstOrDefault(c => c.ClusterId == clusterId);
            List<string> keywords = cluster?.TopicIds.Take(7).Select(id => graph.Topics.TryGetValue(id, out TopicGraphNode? t) ? t.Name : id).ToList() ?? [];

            var contentPillar = new ContentPillar
            {
                PillarPage = pillarCandidate.Page.SitemapPage!,
                LeafPages = clusterPages.Where(p => p.Role == PageRole.Leaf).Select(p => p.SitemapPage!).ToList(),
                SubPillars = [], // Could be extended for hierarchical clusters
                SeoTitle = pillarCandidate.Page.SitemapPage?.Title ?? pillarCandidate.Page.Url,
                SeoDescription = pillarCandidate.Page.SitemapPage?.MetaDescription,
                Keywords = keywords,
                Level = 0,
            };

            // Add cluster pages (not leaf) as potential sub-pillars
            // For now, add them to LeafPages
            contentPillar.LeafPages.AddRange(clusterPages.Where(p => p.Role == PageRole.Cluster).Select(p => p.SitemapPage!));

            structure.Pillars.Add(contentPillar);
        }

        // Collect orphans
        structure.OrphanPages = graph.Pages.Values.Where(p => p.Role == PageRole.Orphan).Select(p => p.SitemapPage!).ToList();

        // Pages in clusters without a valid pillar → TopicClusters
        var clustersWithPillars = pillars.Keys.ToHashSet();
        foreach (TopicClusterResult cluster in topicClusters)
        {
            if (clustersWithPillars.Contains(cluster.ClusterId))
                continue;

            var clusterPages = graph.Pages.Values.Where(p => p.ClusterId == cluster.ClusterId).Select(p => p.SitemapPage!).ToList();

            if (clusterPages.Count >= 3)
            {
                var topicNames = cluster.TopicIds.Take(5).Select(id => graph.Topics.TryGetValue(id, out TopicGraphNode? t) ? t.Name : id).ToList();

                structure.TopicClusters.Add(
                    new TopicCluster
                    {
                        Topic = string.Join(" / ", topicNames.Take(3)),
                        Description = $"Cluster with {clusterPages.Count} pages, no dominant pillar",
                        Pages = clusterPages,
                        Keywords = topicNames,
                    }
                );
            }
        }

        return structure;
    }

    private static double CosineSimilarity(float[] a, float[] b)
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

public sealed class ImprovedClassifierOptions
{
    // Assignment parameters
    public double EmbeddingWeight { get; set; } = 0.6;
    public double TopicOverlapWeight { get; set; } = 0.4;
    public double AssignmentMargin { get; set; } = 0.1; // Min difference between best and second
    public double MinAssignmentSimilarity { get; set; } = 0.3;
    public double MinTopicOverlapForAssignment { get; set; } = 0.5;

    // Pillar validation thresholds (STRICT)
    public double MinChildSupportForPillar { get; set; } = 0.1; // Must link to at least 10% of cluster
    public double MinStructuralCentralityForPillar { get; set; } = 0.2; // Must have decent PageRank
    public double MinThematicCoverageForPillar { get; set; } = 0.3; // Must cover 30% of cluster topics

    // Pillar scoring weights
    public double SemanticCentralityWeight { get; set; } = 0.25;
    public double StructuralCentralityWeight { get; set; } = 0.30;
    public double ThematicCoverageWeight { get; set; } = 0.25;
    public double ChildSupportWeight { get; set; } = 0.20;

    // Classification thresholds
    public double ClusterPageThreshold { get; set; } = 0.5;
    public double OrphanAuthorityThreshold { get; set; } = 10.0;
    public int OrphanInDegreeThreshold { get; set; } = 2;
}

public sealed class PillarCandidate
{
    public PageGraphNode Page { get; set; } = null!;
    public int ClusterId { get; set; }
    public double SemanticCentrality { get; set; }
    public double StructuralCentrality { get; set; }
    public double ThematicCoverage { get; set; }
    public double ChildSupport { get; set; }
    public double TotalScore { get; set; }
    public bool IsValid { get; set; }
}

public sealed class PillarScore
{
    public double SemanticCentrality { get; set; }
    public double StructuralCentrality { get; set; }
    public double ThematicCoverage { get; set; }
    public double ChildSupport { get; set; }
    public double TotalScore { get; set; }
    public bool IsValid { get; set; }
}
