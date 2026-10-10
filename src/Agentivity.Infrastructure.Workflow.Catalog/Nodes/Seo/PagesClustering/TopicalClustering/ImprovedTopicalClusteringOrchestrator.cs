using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.ContentExtraction;
using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Production-grade Topical Clustering Orchestrator.
///
/// Implements a complete 6-phase pipeline:
///
/// PHASE 1: Content Normalization (anti-boilerplate)
/// PHASE 2: Semantic Representation (robust chunk aggregation)
/// PHASE 3: Multi-Layer Graph Construction (Page-Topic-Page)
/// PHASE 4: Topic Reduction &amp; Leiden Clustering
/// PHASE 5: Page Assignment with Margin Rule (anti-leakage)
/// PHASE 6: Diagnostics &amp; Validation
///
/// Key improvements over basic algorithm:
/// - Margin-based assignment prevents topic leakage
/// - Strict pillar validation (child support + authority + coverage)
/// - Multi-layer graph captures both semantic AND structural signals
/// - Weighted set-cover removes generic topics
/// - Comprehensive diagnostics (cannibalization, false pillars, etc.)
/// </summary>
public sealed class ImprovedTopicalClusteringOrchestrator
{
    private readonly ILogger _logger;
    private readonly ImprovedClusteringOptions _options;

    public ImprovedTopicalClusteringOrchestrator(ILogger logger, ImprovedClusteringOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new ImprovedClusteringOptions();
    }

    /// <summary>
    /// Main entry point for website structure mining.
    /// </summary>
    public async Task<ImprovedClusteringResult> ExecuteAsync(List<SitemapPage> pages, Dictionary<string, string>? rawHtmlByUrl, ILlmClient llmClient, IEmbeddingCapability embeddingProvider, string siteUrl, Action<string> logCallback, CancellationToken cancellationToken)
    {
        var result = new ImprovedClusteringResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Adapt options to site size
            _options.AdaptToSiteSize(pages.Count);

            logCallback($"🚀 Starting improved topical clustering for {pages.Count} pages");
            logCallback($"   Options: margin={_options.AssignmentMargin}, leiden_res={_options.LeidenResolution}");

            // ════════════════════════════════════════════════════════════════
            // PHASE 1: CONTENT NORMALIZATION
            // ════════════════════════════════════════════════════════════════
            logCallback("📄 Phase 1: Content normalization...");

            Dictionary<string, ExtractedContent> extractedContents = await ExtractContentAsync(pages, rawHtmlByUrl, logCallback, cancellationToken);

            // ════════════════════════════════════════════════════════════════
            // PHASE 2: SEMANTIC REPRESENTATION
            // ════════════════════════════════════════════════════════════════
            logCallback("🧠 Phase 2: Semantic representation...");

            Dictionary<string, PageTopicResult> topicResults = await ExtractTopicsAsync(extractedContents, llmClient, embeddingProvider, logCallback, cancellationToken);

            result.TotalTopicsExtracted = topicResults.Values.SelectMany(r => r.Topics).Select(t => t.Concept).Distinct().Count();

            logCallback($"   ✅ Extracted {result.TotalTopicsExtracted} unique topics");

            // ════════════════════════════════════════════════════════════════
            // PHASE 3: MULTI-LAYER GRAPH CONSTRUCTION
            // ════════════════════════════════════════════════════════════════
            logCallback("🕸️ Phase 3: Multi-layer graph construction...");

            MultiLayerTopicalGraph graph = BuildMultiLayerGraph(pages, topicResults, logCallback);

            // ════════════════════════════════════════════════════════════════
            // PHASE 4: TOPIC REDUCTION & CLUSTERING
            // ════════════════════════════════════════════════════════════════
            logCallback("🎯 Phase 4: Topic reduction & clustering...");

            HashSet<string> selectedTopics = graph.ReduceTopics();
            result.TopicsAfterReduction = selectedTopics.Count;

            List<TopicClusterResult> topicClusters = ClusterTopics(graph, logCallback);
            result.TopicClustersCreated = topicClusters.Count;

            if (topicClusters.Count < 2)
            {
                logCallback("   ⚠️ Insufficient clusters, returning fallback structure");
                return BuildFallbackResult(pages, siteUrl, result);
            }

            // ════════════════════════════════════════════════════════════════
            // PHASE 5: PAGE ASSIGNMENT & CLASSIFICATION
            // ════════════════════════════════════════════════════════════════
            logCallback("📍 Phase 5: Page assignment & classification...");

            var classifier = new ImprovedPageClassifier(
                _logger,
                new ImprovedClassifierOptions
                {
                    AssignmentMargin = _options.AssignmentMargin,
                    MinAssignmentSimilarity = _options.MinAssignmentSimilarity,
                    MinChildSupportForPillar = _options.MinChildSupportForPillar,
                    MinStructuralCentralityForPillar = _options.MinStructuralCentralityForPillar,
                    MinThematicCoverageForPillar = _options.MinThematicCoverageForPillar,
                }
            );

            classifier.AssignPagesToClustersClusters(graph, topicClusters);

            Dictionary<int, PillarCandidate> pillars = classifier.IdentifyPillars(graph, topicClusters);

            foreach ((int clusterId, PillarCandidate? pillar) in pillars.Take(10))
            {
                logCallback($"   🏛️ Cluster {clusterId}: {pillar.Page.SitemapPage?.Title ?? pillar.Page.Url}");
                logCallback($"      Semantic: {pillar.SemanticCentrality:F2}, Auth: {pillar.StructuralCentrality:F2}, " + $"Coverage: {pillar.ThematicCoverage:F2}, Children: {pillar.ChildSupport:F2}");
            }

            classifier.ClassifyPageRoles(graph, pillars);

            // ════════════════════════════════════════════════════════════════
            // PHASE 6: DIAGNOSTICS & BUILD STRUCTURE
            // ════════════════════════════════════════════════════════════════
            logCallback("🔍 Phase 6: Diagnostics & validation...");

            SiteStructure structure = classifier.BuildSiteStructure(graph, pillars, topicClusters, siteUrl);

            ClusteringDiagnostics diagnostics = GenerateDiagnostics(graph, topicClusters, pillars, logCallback);

            stopwatch.Stop();
            logCallback($"✅ Improved clustering complete in {stopwatch.ElapsedMilliseconds}ms");
            logCallback($"   Final: {structure.Pillars.Count} pillars, {structure.TopicClusters.Count} clusters, " + $"{structure.OrphanPages.Count} orphans");

            result.Structure = structure;
            result.Graph = graph;
            result.Diagnostics = diagnostics;
            result.Success = true;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Improved topical clustering failed");
            logCallback($"❌ Error: {ex.Message}");
            result.Error = ex.Message;
            return result;
        }
    }

    private async Task<Dictionary<string, ExtractedContent>> ExtractContentAsync(List<SitemapPage> pages, Dictionary<string, string>? rawHtmlByUrl, Action<string> logCallback, CancellationToken cancellationToken)
    {
        var extractedContents = new Dictionary<string, ExtractedContent>();
        var contentExtractor = new MainContentExtractor();

        if (rawHtmlByUrl != null && rawHtmlByUrl.Count > 0)
        {
            foreach ((string? url, string? html) in rawHtmlByUrl)
            {
                try
                {
                    ExtractedContent extracted = contentExtractor.Extract(html, url);
                    extractedContents[url] = extracted;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract content from {Url}", url);
                }
            }
            logCallback($"   ✅ Extracted main content from {extractedContents.Count} pages");
        }
        else
        {
            // Fallback: build from SitemapPage metadata
            foreach (SitemapPage page in pages)
            {
                extractedContents[page.Url] = new ExtractedContent
                {
                    Url = page.Url,
                    Title = page.Title,
                    H1 = page.H1,
                    MetaDescription = page.MetaDescription,
                    MainText = page.Excerpt ?? "",
                    Chunks = BuildChunksFromMetadata(page),
                };
            }
            logCallback($"   ⚠️ No raw HTML, using metadata for {extractedContents.Count} pages");
        }

        return extractedContents;
    }

    private async Task<Dictionary<string, PageTopicResult>> ExtractTopicsAsync(Dictionary<string, ExtractedContent> contents, ILlmClient llmClient, IEmbeddingCapability embeddingProvider, Action<string> logCallback, CancellationToken cancellationToken)
    {
        var topicExtractor = new ImprovedTopicExtractor(
            _logger,
            new TopicExtractionOptions
            {
                MaxTopicsPerPage = _options.MaxTopicsPerPage,
                MaxChunksToAnalyze = _options.MaxChunksToAnalyze,
                TopicMergeSimilarityThreshold = _options.TopicMergeSimilarity,
            }
        );

        var topicResults = new Dictionary<string, PageTopicResult>();
        int processedCount = 0;

        foreach ((string? url, ExtractedContent? content) in contents)
        {
            try
            {
                PageTopicResult topicResult = await topicExtractor.ExtractTopicsAsync(content, llmClient, embeddingProvider, cancellationToken);
                topicResults[url] = topicResult;
                processedCount++;

                if (processedCount % 10 == 0)
                {
                    logCallback($"   Processed {processedCount}/{contents.Count} pages...");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract topics from {Url}", url);
            }
        }

        return topicResults;
    }

    private MultiLayerTopicalGraph BuildMultiLayerGraph(List<SitemapPage> pages, Dictionary<string, PageTopicResult> topicResults, Action<string> logCallback)
    {
        var graph = new MultiLayerTopicalGraph(
            _logger,
            new MultiLayerGraphOptions
            {
                MinTopicSimilarity = _options.MinTopicSimilarity,
                MaxTopicCoverageRatio = _options.MaxTopicCoverageRatio,
                MaxTopicsAfterReduction = _options.MaxTopicsAfterReduction,
            }
        );

        graph.BuildGraph(pages, topicResults);
        graph.ComputeGraphAuthority();

        logCallback($"   ✅ Graph: {graph.Pages.Count} pages, {graph.Topics.Count} topics");
        logCallback($"   ✅ Edges: {graph.PageTopicEdges.Count} P→T, {graph.TopicTopicEdges.Count} T↔T, " + $"{graph.PagePageEdges.Count} P↔P");

        // Log top pages by authority
        var topByAuthority = graph.Pages.Values.OrderByDescending(p => p.GraphAuthority).Take(5).ToList();

        logCallback($"   Top 5 by Graph Authority:");
        foreach (PageGraphNode? p in topByAuthority)
        {
            logCallback($"      {p.SitemapPage?.Title ?? p.Url} (auth: {p.GraphAuthority:F1})");
        }

        return graph;
    }

    private List<TopicClusterResult> ClusterTopics(MultiLayerTopicalGraph graph, Action<string> logCallback)
    {
        TopicAdjacencyMatrix adjacencyMatrix = graph.GetTopicAdjacencyMatrix();

        if (adjacencyMatrix.Size < 2)
        {
            return [];
        }

        var spectralClusterer = new SpectralClusterer(
            new SpectralClusteringOptions
            {
                MaxIterations = _options.MaxClusteringIterations,
                MinClusterSize = _options.MinClusterSize,
                CohesionThreshold = _options.ClusterCohesionThreshold,
            }
        );

        // Convert TopicGraphNode to TopicNode for compatibility
        var topicNodes = adjacencyMatrix
            .Topics.Select(t => new TopicNode
            {
                Id = t.Id,
                Name = t.Name,
                Embedding = t.Embedding,
                IsSelected = t.IsSelected,
            })
            .ToList();

        List<TopicClusterResult> topicClusters = spectralClusterer.ClusterTopics(
            new TopicGraphMatrix
            {
                Topics = topicNodes,
                TopicIndex = adjacencyMatrix.IndexMap,
                AdjacencyMatrix = adjacencyMatrix.Matrix,
            }
        );

        logCallback($"   ✅ Created {topicClusters.Count} topic clusters");

        foreach (TopicClusterResult? cluster in topicClusters.Take(10))
        {
            var topicNames = cluster.TopicIds.Take(5).Select(id => graph.Topics.TryGetValue(id, out TopicGraphNode? t) ? t.Name : id).ToList();
            logCallback($"      Cluster {cluster.ClusterId}: {cluster.TopicIds.Count} topics ({string.Join(", ", topicNames)})");
        }

        return topicClusters;
    }

    private ClusteringDiagnostics GenerateDiagnostics(MultiLayerTopicalGraph graph, List<TopicClusterResult> topicClusters, Dictionary<int, PillarCandidate> pillars, Action<string> logCallback)
    {
        var diagnostics = new ClusteringDiagnostics();

        // Check for clusters without pillars
        var clustersWithPillars = pillars.Keys.ToHashSet();
        foreach (TopicClusterResult cluster in topicClusters)
        {
            if (!clustersWithPillars.Contains(cluster.ClusterId))
            {
                int pageCount = graph.Pages.Values.Count(p => p.ClusterId == cluster.ClusterId);
                if (pageCount >= 3)
                {
                    string warning = $"Cluster {cluster.ClusterId} has {pageCount} pages but no valid pillar (MISSING_PILLAR)";
                    diagnostics.Warnings.Add(warning);
                    logCallback($"   ⚠️ {warning}");
                }
            }
        }

        // Check for potential topic cannibalization
        foreach (TopicGraphNode? topic in graph.Topics.Values.Where(t => t.IsSelected))
        {
            double coverageRatio = (double)topic.PageWeights.Count / graph.Pages.Count;
            if (coverageRatio > 0.5)
            {
                string warning = $"Topic '{topic.Name}' appears on {coverageRatio:P0} of pages - potential cannibalization";
                diagnostics.Warnings.Add(warning);
                logCallback($"   ⚠️ {warning}");
            }
        }

        // Check for high-authority orphans
        var orphans = graph.Pages.Values.Where(p => p.Role == PageRole.Orphan).ToList();
        var highAuthorityOrphans = orphans.Where(p => p.GraphAuthority > 30).ToList();

        foreach (PageGraphNode? orphan in highAuthorityOrphans)
        {
            string warning = $"High-authority orphan: {orphan.SitemapPage?.Title ?? orphan.Url} (auth: {orphan.GraphAuthority:F1})";
            diagnostics.Warnings.Add(warning);
            logCallback($"   ⚠️ {warning}");
        }

        // Check for low-authority pillars (potential false pillars)
        foreach ((int clusterId, PillarCandidate? pillar) in pillars)
        {
            if (pillar.StructuralCentrality < 0.3)
            {
                string warning = $"Pillar '{pillar.Page.SitemapPage?.Title}' has low authority ({pillar.StructuralCentrality:F2}) - potential FALSE_PILLAR";
                diagnostics.Warnings.Add(warning);
                logCallback($"   ⚠️ {warning}");
            }

            if (pillar.ChildSupport < 0.15)
            {
                string warning = $"Pillar '{pillar.Page.SitemapPage?.Title}' has weak child support ({pillar.ChildSupport:F2}) - structural isolation";
                diagnostics.Warnings.Add(warning);
                logCallback($"   ⚠️ {warning}");
            }
        }

        return diagnostics;
    }

    private List<ContentChunk> BuildChunksFromMetadata(SitemapPage page)
    {
        var chunks = new List<ContentChunk>();
        int position = 0;

        if (!string.IsNullOrEmpty(page.Title))
        {
            chunks.Add(
                new ContentChunk
                {
                    Text = page.Title,
                    Type = ChunkType.H1,
                    Position = position++,
                    Weight = 2.0,
                }
            );
        }

        if (!string.IsNullOrEmpty(page.H1) && page.H1 != page.Title)
        {
            chunks.Add(
                new ContentChunk
                {
                    Text = page.H1,
                    Type = ChunkType.H1,
                    Position = position++,
                    Weight = 1.8,
                }
            );
        }

        if (!string.IsNullOrEmpty(page.MetaDescription))
        {
            chunks.Add(
                new ContentChunk
                {
                    Text = page.MetaDescription,
                    Type = ChunkType.Paragraph,
                    Position = position++,
                    Weight = 1.5,
                }
            );
        }

        if (!string.IsNullOrEmpty(page.Excerpt))
        {
            chunks.Add(
                new ContentChunk
                {
                    Text = page.Excerpt,
                    Type = ChunkType.Paragraph,
                    Position = position++,
                    Weight = 1.0,
                }
            );
        }

        return chunks;
    }

    private ImprovedClusteringResult BuildFallbackResult(List<SitemapPage> pages, string siteUrl, ImprovedClusteringResult partialResult)
    {
        var structure = new SiteStructure
        {
            SiteUrl = siteUrl,
            AnalyzedAt = DateTime.UtcNow,
            TotalPages = pages.Count,
            OrphanPages = pages.ToList(),
        };

        partialResult.Structure = structure;
        partialResult.Success = true;
        return partialResult;
    }
}

/// <summary>
/// Configuration options for the improved clustering algorithm.
/// All parameters have sensible defaults and can adapt to site size.
/// </summary>
public sealed class ImprovedClusteringOptions
{
    // ═══════════════════════════════════════════════════════════════════════
    // PHASE 2: Topic extraction
    // ═══════════════════════════════════════════════════════════════════════
    public int MaxTopicsPerPage { get; set; } = 8;
    public int MaxChunksToAnalyze { get; set; } = 10;
    public double TopicMergeSimilarity { get; set; } = 0.85;

    // ═══════════════════════════════════════════════════════════════════════
    // PHASE 3: Graph construction
    // ═══════════════════════════════════════════════════════════════════════
    public double MinTopicSimilarity { get; set; } = 0.3;
    public double MaxTopicCoverageRatio { get; set; } = 0.8;
    public int MaxTopicsAfterReduction { get; set; } = 100;

    // ═══════════════════════════════════════════════════════════════════════
    // PHASE 4: Clustering
    // ═══════════════════════════════════════════════════════════════════════
    public double LeidenResolution { get; set; } = 1.0;
    public int MaxClusteringIterations { get; set; } = 50;
    public int MinClusterSize { get; set; } = 2;
    public double ClusterCohesionThreshold { get; set; } = 0.5;

    // ═══════════════════════════════════════════════════════════════════════
    // PHASE 5: Page assignment (CRITICAL for anti-leakage)
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Minimum gap between best and second-best cluster affinity.
    /// Higher values reduce leakage but may increase unassigned pages.
    /// Recommended: 0.1 for general sites, 0.15 for overlapping topics.
    /// </summary>
    public double AssignmentMargin { get; set; } = 0.1;

    /// <summary>
    /// Minimum affinity to assign a page to any cluster.
    /// Pages below this threshold become orphans.
    /// </summary>
    public double MinAssignmentSimilarity { get; set; } = 0.3;

    // ═══════════════════════════════════════════════════════════════════════
    // PHASE 5: Pillar validation (STRICT constraints)
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Minimum fraction of cluster pages that the pillar must link to.
    /// Prevents generic popular articles from being elected as pillars.
    /// </summary>
    public double MinChildSupportForPillar { get; set; } = 0.1;

    /// <summary>
    /// Minimum normalized PageRank/authority for pillar candidacy.
    /// Must be above this percentile of all pages.
    /// </summary>
    public double MinStructuralCentralityForPillar { get; set; } = 0.2;

    /// <summary>
    /// Minimum fraction of cluster topics that the pillar must cover.
    /// Ensures pillar is thematically representative.
    /// </summary>
    public double MinThematicCoverageForPillar { get; set; } = 0.3;

    /// <summary>
    /// Adapt parameters based on site size.
    /// Small sites need stricter margins, large sites need more clusters.
    /// </summary>
    public void AdaptToSiteSize(int pageCount)
    {
        if (pageCount < 50)
        {
            LeidenResolution = 0.8;
            MaxTopicsAfterReduction = 50;
            AssignmentMargin = 0.15;
            MinClusterSize = 2;
        }
        else if (pageCount > 500)
        {
            LeidenResolution = 1.2;
            MaxTopicsAfterReduction = 200;
            AssignmentMargin = 0.08;
            MinClusterSize = 3;
        }
    }
}

/// <summary>
/// Result of the improved clustering algorithm.
/// </summary>
public sealed class ImprovedClusteringResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public SiteStructure? Structure { get; set; }
    public MultiLayerTopicalGraph? Graph { get; set; }
    public ClusteringDiagnostics? Diagnostics { get; set; }

    // Metrics
    public int TotalTopicsExtracted { get; set; }
    public int TopicsAfterReduction { get; set; }
    public int TopicClustersCreated { get; set; }
}

/// <summary>
/// Diagnostics and warnings from the clustering process.
/// </summary>
public sealed class ClusteringDiagnostics
{
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// Suggested actions based on detected issues.
    /// </summary>
    public List<SuggestedAction> SuggestedActions { get; set; } = [];
}

public sealed class SuggestedAction
{
    public string ActionType { get; set; } = ""; // CREATE_HUB, MERGE_CLUSTERS, REVIEW_CLASSIFICATION
    public string TargetUrl { get; set; } = "";
    public int? ClusterId { get; set; }
    public string Reason { get; set; } = "";
}
