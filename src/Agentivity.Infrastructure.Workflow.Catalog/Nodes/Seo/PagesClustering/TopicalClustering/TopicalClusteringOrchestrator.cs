using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Main orchestrator for the Topical Clustering algorithm.
/// Implements the complete pipeline from the paper "Topical Clustering of Search Results" (Scaiella et al.)
///
/// Pipeline:
/// 1. Semantic annotation (TAGME equivalent)
/// 2. Topic graph construction
/// 3. Set-cover topic reduction
/// 4. Spectral clustering of topics
/// 5. Cluster-to-page projection
/// 6. Pillar identification
/// 7. Page classification
/// 8. Structure output
/// </summary>
public sealed class TopicalClusteringOrchestrator
{
    private readonly ILogger _logger;
    private readonly TopicalClusteringOptions _options;

    public TopicalClusteringOrchestrator(ILogger logger, TopicalClusteringOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new TopicalClusteringOptions();
    }

    /// <summary>
    /// Execute the complete topical clustering pipeline.
    /// </summary>
    public async Task<TopicalClusteringResult> ExecuteAsync(List<SitemapPage> pages, ILlmProvider llmProvider, ResourceBinding llmBinding, IEmbeddingProvider embeddingProvider, ResourceBinding embeddingBinding, string siteUrl, Action<string> logCallback, CancellationToken cancellationToken)
    {
        var result = new TopicalClusteringResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // ===== STEP 1: Semantic Annotation =====
            logCallback("📝 Step 1: Semantic topic annotation (TAGME equivalent)...");

            var annotator = new SemanticTopicAnnotator(new SemanticAnnotatorOptions { MaxTopicsPerPage = _options.MaxTopicsPerPage, MinSimilarityThreshold = _options.MinTopicSimilarity }, _logger);

            TopicalGraph graph = await annotator.BuildTopicalGraphAsync(pages, async (text, url, ct) => await ExtractTopicsWithLlmAsync(llmProvider, llmBinding, text, url, ct), new EmbeddingProviderAdapter(embeddingProvider, embeddingBinding), cancellationToken);

            logCallback($"   ✅ Built graph: {graph.Pages.Count} pages, {graph.Topics.Count} topics, {graph.TopicSimilarities.Count} edges");
            result.TotalTopicsExtracted = graph.Topics.Count;

            // ===== STEP 2-3: Topic Reduction (Set-Cover) =====
            logCallback("🎯 Step 2-3: Topic reduction via weighted set-cover...");

            var setCoverSolver = new TopicSetCoverSolver(
                new TopicSetCoverOptions
                {
                    MaxTopics = _options.MaxTopicsAfterReduction,
                    MaxCoverageRatio = _options.MaxTopicCoverageRatio,
                    DiversityPenalty = _options.TopicDiversityPenalty,
                }
            );

            HashSet<string> selectedTopics = setCoverSolver.SolveWithDiversity(graph);
            logCallback($"   ✅ Reduced to {selectedTopics.Count} topics (from {graph.Topics.Count})");
            result.TopicsAfterReduction = selectedTopics.Count;

            // ===== STEP 4: Build Reduced Topic Graph Gₜ =====
            logCallback("📊 Step 4: Building reduced topic graph Gₜ...");

            TopicGraphMatrix topicGraph = graph.GetReducedTopicGraph();
            logCallback($"   ✅ Topic graph: {topicGraph.Size} nodes");

            if (topicGraph.Size < 2)
            {
                logCallback("   ⚠️ Not enough topics for clustering. Returning basic structure.");
                return BuildFallbackResult(pages, siteUrl, graph, result);
            }

            // ===== STEP 5: Spectral Clustering =====
            logCallback("🔬 Step 5: Spectral clustering of topic graph...");

            var spectralClusterer = new SpectralClusterer(
                new SpectralClusteringOptions
                {
                    MaxIterations = _options.MaxClusteringIterations,
                    MinClusterSize = _options.MinClusterSize,
                    CohesionThreshold = _options.ClusterCohesionThreshold,
                }
            );

            List<TopicClusterResult> topicClusters = spectralClusterer.ClusterTopics(topicGraph);
            logCallback($"   ✅ Created {topicClusters.Count} topic clusters");
            result.TopicClustersCreated = topicClusters.Count;

            // Log cluster details
            foreach (TopicClusterResult? cluster in topicClusters.Take(10))
            {
                var topicNames = cluster.TopicIds.Take(5).Select(id => graph.Topics.TryGetValue(id, out TopicNode? t) ? t.Name : id).ToList();
                logCallback($"      Cluster {cluster.ClusterId}: {cluster.TopicIds.Count} topics ({string.Join(", ", topicNames)})");
            }

            // ===== STEP 6: Project Clusters to Pages =====
            logCallback("📄 Step 6: Projecting topic clusters to pages...");

            var pageClassifier = new PageClassifier(
                new PageClassifierOptions
                {
                    OrphanThreshold = _options.OrphanThreshold,
                    LeafThreshold = _options.LeafThreshold,
                    ClusterThreshold = _options.ClusterThreshold,
                }
            );

            pageClassifier.ProjectClustersToPages(graph, topicClusters);

            int pagesWithClusters = graph.Pages.Values.Count(p => p.ClusterIds.Count > 0);
            int pagesWithMultiple = graph.Pages.Values.Count(p => p.ClusterIds.Count > 1);
            logCallback($"   ✅ {pagesWithClusters} pages assigned to clusters ({pagesWithMultiple} with multiple memberships)");

            // ===== STEP 7: Identify Pillar Pages =====
            logCallback("🏛️ Step 7: Identifying pillar pages (h(Tᵢ) maximization)...");

            Dictionary<int, string> clusterPillars = pageClassifier.IdentifyPillarPages(graph, topicClusters);
            logCallback($"   ✅ Identified {clusterPillars.Count} pillar pages");

            foreach ((int clusterId, string? pillarUrl) in clusterPillars.Take(10))
            {
                PageNode? page = graph.Pages.TryGetValue(pillarUrl, out PageNode? p) ? p : null;
                double score = page?.PillarScores.GetValueOrDefault(clusterId, 0) ?? 0;
                logCallback($"      Cluster {clusterId} pillar: {page?.SitemapPage.Title ?? pillarUrl} (h={score:F2})");
            }

            // ===== STEP 8: Classify All Pages =====
            logCallback("🏷️ Step 8: Classifying all pages...");

            pageClassifier.ClassifyPages(graph, topicClusters, clusterPillars);

            var classification = graph.Pages.Values.GroupBy(p => p.Classification).ToDictionary(g => g.Key, g => g.Count());

            logCallback($"   ✅ Classification:");
            logCallback($"      - Pillar: {classification.GetValueOrDefault(PageClassification.Pillar, 0)}");
            logCallback($"      - Cluster: {classification.GetValueOrDefault(PageClassification.Cluster, 0)}");
            logCallback($"      - Leaf: {classification.GetValueOrDefault(PageClassification.Leaf, 0)}");
            logCallback($"      - Orphan: {classification.GetValueOrDefault(PageClassification.Orphan, 0)}");

            // ===== STEP 9: Build Final Structure =====
            logCallback("🏗️ Step 9: Building final site structure...");

            SiteStructure structure = pageClassifier.BuildSiteStructure(graph, topicClusters, clusterPillars, siteUrl);

            stopwatch.Stop();
            logCallback($"✅ Topical clustering complete in {stopwatch.ElapsedMilliseconds}ms");
            logCallback($"   Final: {structure.Pillars.Count} pillars, {structure.TopicClusters.Count} topic clusters, {structure.OrphanPages.Count} orphans");

            result.Structure = structure;
            result.Graph = graph;
            result.TopicClusters = topicClusters;
            result.ClusterPillars = clusterPillars;
            result.Success = true;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Topical clustering failed");
            logCallback($"❌ Error: {ex.Message}");
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Extract topics from page text using LLM.
    /// This is the TAGME equivalent - extracts disambiguated concepts with weights.
    /// </summary>
    private async Task<TopicExtractionResult> ExtractTopicsWithLlmAsync(ILlmProvider llmProvider, ResourceBinding llmBinding, string pageText, string? url, CancellationToken cancellationToken)
    {
        string prompt = $"""
            Extract the main semantic topics (concepts) from this web page content.

            Content:
            {pageText}

            Extract {_options.MaxTopicsPerPage} distinct topics that represent the main concepts discussed.
            Each topic should be:
            - A disambiguated concept (e.g., "machine learning" not just "learning")
            - Specific enough to be meaningful
            - Weighted by importance (0.0-1.0)

            Return ONLY the topics that are genuinely present in the content.
            """;

        string schema = """
            {
                "type": "object",
                "properties": {
                    "topics": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "topic": { "type": "string" },
                                "weight": { "type": "number" }
                            },
                            "required": ["topic", "weight"]
                        }
                    }
                },
                "required": ["topics"]
            }
            """;

        OperationResult<LlmResponse> result = await llmProvider.CompleteAsync(
            llmBinding,
            prompt,
            new LlmRequestOptions(SystemPrompt: "You are a semantic annotation system. Extract disambiguated concepts with importance weights.", Temperature: 0.2, MaxTokens: 500, ResponseSchema: schema),
            cancellationToken
        );

        if (result.IsFailure || string.IsNullOrWhiteSpace(result.Value?.Content))
        {
            // Fallback: extract from URL
            return ExtractTopicsFromUrl(url ?? "");
        }

        try
        {
            LlmTopicResponse? parsed = JsonSerializer.Deserialize<LlmTopicResponse>(result.Value.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new TopicExtractionResult { Topics = parsed?.Topics?.Select(t => new TopicWeight { Topic = t.Topic, Weight = Math.Clamp(t.Weight, 0.0, 1.0) }).ToList() ?? [] };
        }
        catch
        {
            return ExtractTopicsFromUrl(url ?? "");
        }
    }

    private TopicExtractionResult ExtractTopicsFromUrl(string url)
    {
        var topics = new List<TopicWeight>();

        try
        {
            string path = new Uri(url).AbsolutePath.Trim('/');
            string[] segments = path.Split('/');

            double weight = 1.0;
            foreach (string segment in segments)
            {
                string topic = segment.Replace('-', ' ').Replace('_', ' ');
                if (!string.IsNullOrWhiteSpace(topic) && topic.Length > 2)
                {
                    topics.Add(new TopicWeight { Topic = topic, Weight = weight });
                    weight *= 0.8; // Decrease weight for deeper segments
                }
            }
        }
        catch { }

        return new TopicExtractionResult { Topics = topics };
    }

    private TopicalClusteringResult BuildFallbackResult(List<SitemapPage> pages, string siteUrl, TopicalGraph graph, TopicalClusteringResult partialResult)
    {
        // Simple fallback when clustering fails
        var structure = new SiteStructure
        {
            SiteUrl = siteUrl,
            AnalyzedAt = DateTime.UtcNow,
            TotalPages = pages.Count,
            OrphanPages = pages.ToList(),
        };

        partialResult.Structure = structure;
        partialResult.Graph = graph;
        partialResult.Success = true;
        return partialResult;
    }

    private class LlmTopicResponse
    {
        public List<LlmTopic>? Topics { get; set; }
    }

    private class LlmTopic
    {
        public string Topic { get; set; } = string.Empty;
        public double Weight { get; set; }
    }
}

/// <summary>
/// Options for the topical clustering algorithm
/// </summary>
public sealed class TopicalClusteringOptions
{
    // Step 1: Annotation
    public int MaxTopicsPerPage { get; set; } = 8;
    public double MinTopicSimilarity { get; set; } = 0.3;

    // Step 3: Set-Cover Reduction
    public int MaxTopicsAfterReduction { get; set; } = 100;
    public double MaxTopicCoverageRatio { get; set; } = 0.8;
    public double TopicDiversityPenalty { get; set; } = 0.3;

    // Step 5: Spectral Clustering
    public int MaxClusteringIterations { get; set; } = 50;
    public int MinClusterSize { get; set; } = 2;
    public double ClusterCohesionThreshold { get; set; } = 0.5;

    // Step 8: Classification
    public double OrphanThreshold { get; set; } = 0.3;
    public double LeafThreshold { get; set; } = 0.6;
    public double ClusterThreshold { get; set; } = 0.5;

    // Step 7: Page Assignment (anti-leakage - NEW)
    public double AssignmentMargin { get; set; } = 0.1;

    // Step 8: Pillar Validation (strict - NEW)
    public double MinChildSupportForPillar { get; set; } = 0.1;
}

/// <summary>
/// Complete result of topical clustering
/// </summary>
public sealed class TopicalClusteringResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    public SiteStructure? Structure { get; set; }
    public TopicalGraph? Graph { get; set; }
    public List<TopicClusterResult>? TopicClusters { get; set; }
    public Dictionary<int, string>? ClusterPillars { get; set; }

    // Statistics
    public int TotalTopicsExtracted { get; set; }
    public int TopicsAfterReduction { get; set; }
    public int TopicClustersCreated { get; set; }
}

/// <summary>
/// Bridges IEmbeddingProvider (resource node) to IEmbeddingCapability (internal clustering classes).
/// Avoids rewriting all downstream clustering code.
/// </summary>
internal sealed class EmbeddingProviderAdapter : IEmbeddingCapability
{
    private readonly IEmbeddingProvider _provider;
    private readonly ResourceBinding _binding;

    public EmbeddingProviderAdapter(IEmbeddingProvider provider, ResourceBinding binding)
    {
        _provider = provider;
        _binding = binding;
    }

    public int EmbeddingDimensions => _provider.GetEmbeddingDimensions(_binding);
    public string ModelName => _provider.GetModelName(_binding);

    public Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken = default) => _provider.CreateEmbeddingAsync(_binding, text, cancellationToken);

    public Task<List<float[]>> CreateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) => _provider.CreateBatchEmbeddingsAsync(_binding, texts, cancellationToken);
}
