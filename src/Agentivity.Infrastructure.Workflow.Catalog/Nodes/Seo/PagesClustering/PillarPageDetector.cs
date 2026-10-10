using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering;

/// <summary>
/// Options for pillar page detection algorithm.
/// Each heuristic can be enabled/disabled for experimentation.
/// </summary>
public sealed class PillarPageDetectorOptions
{
    // ===== ENABLE/DISABLE HEURISTICS =====

    /// <summary>Enable PageRank calculation (internal authority)</summary>
    public bool EnablePageRank { get; set; } = true;

    /// <summary>Enable semantic breadth analysis (topic diversity)</summary>
    public bool EnableSemanticBreadth { get; set; } = false; // Requires embeddings

    /// <summary>Enable inbound link count analysis</summary>
    public bool EnableInboundLinks { get; set; } = true;

    /// <summary>Enable outbound link count analysis</summary>
    public bool EnableOutboundLinks { get; set; } = true;

    /// <summary>Enable structural analysis (TOC, H2 count, word count)</summary>
    public bool EnableStructure { get; set; } = true;

    /// <summary>Enable URL depth analysis (proximity to homepage)</summary>
    public bool EnableDepth { get; set; } = true;

    // ===== WEIGHTS (must sum to 1.0) =====
    // Optimized for internal linking structure analysis (focus on PageRank + link centrality)

    public double PageRankWeight { get; set; } = 0.45; // Primary: Internal PageRank authority
    public double SemanticBreadthWeight { get; set; } = 0.05; // Minimal (requires embeddings)
    public double InboundLinksWeight { get; set; } = 0.35; // Primary: Internal link centrality
    public double OutboundLinksWeight { get; set; } = 0.05; // Secondary: Hub pages
    public double StructureWeight { get; set; } = 0.05; // Secondary: Content depth
    public double DepthWeight { get; set; } = 0.05; // Secondary: URL proximity

    // ===== THRESHOLDS =====

    /// <summary>Minimum score (0-100) to be considered a pillar page</summary>
    public double PillarScoreThreshold { get; set; } = 70.0;

    /// <summary>Percentile threshold (alternative to fixed score)</summary>
    public double PillarPercentile { get; set; } = 0.85; // Top 15%

    /// <summary>Use percentile instead of fixed threshold</summary>
    public bool UsePercentileThreshold { get; set; } = true;

    // ===== PAGERANK PARAMETERS =====

    public int PageRankIterations { get; set; } = 20;
    public double PageRankDampingFactor { get; set; } = 0.85;

    public void Validate()
    {
        double totalWeight = PageRankWeight + SemanticBreadthWeight + InboundLinksWeight + OutboundLinksWeight + StructureWeight + DepthWeight;

        if (Math.Abs(totalWeight - 1.0) > 0.01)
        {
            throw new InvalidOperationException($"Weights must sum to 1.0 (current: {totalWeight})");
        }
    }
}

/// <summary>
/// Detects pillar pages using multiple heuristics inspired by Google's algorithm.
/// Each heuristic can be enabled/disabled for experimentation.
/// </summary>
public sealed class PillarPageDetector
{
    private readonly PillarPageDetectorOptions _options;
    private readonly ILogger? _logger;

    public PillarPageDetector(PillarPageDetectorOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new PillarPageDetectorOptions();
        _options.Validate();
        _logger = logger;
    }

    /// <summary>
    /// Analyzes all pages and computes pillar scores
    /// </summary>
    public void AnalyzePages(List<SitemapPage> pages)
    {
        if (pages == null || pages.Count == 0)
            return;

        _logger?.LogInformation("Analyzing {PageCount} pages for pillar detection...", pages.Count);

        // 1. Calculate PageRank (internal authority)
        if (_options.EnablePageRank)
        {
            _logger?.LogInformation("✓ Computing PageRank...");
            CalculatePageRank(pages);
        }

        // 2. Calculate inbound/outbound link counts
        if (_options.EnableInboundLinks || _options.EnableOutboundLinks)
        {
            _logger?.LogInformation("✓ Analyzing link graph...");
            CalculateLinkMetrics(pages);
        }

        // 3. Analyze structure (depth, H2 count, etc.)
        if (_options.EnableStructure)
        {
            _logger?.LogInformation("✓ Analyzing page structure...");
            AnalyzeStructure(pages);
        }

        // 4. Calculate URL depth
        if (_options.EnableDepth)
        {
            _logger?.LogInformation("✓ Calculating URL depth...");
            CalculateDepth(pages);
        }

        // 5. Semantic breadth (if enabled and embeddings available)
        if (_options.EnableSemanticBreadth)
        {
            _logger?.LogInformation("⚠ Semantic breadth analysis not implemented yet");
            // TODO: Implement when we have section embeddings
        }

        // 6. Compute final pillar scores
        _logger?.LogInformation("✓ Computing final pillar scores...");
        ComputePillarScores(pages);

        // 7. Identify pillar pages
        IdentifyPillarPages(pages);
    }

    private void CalculatePageRank(List<SitemapPage> pages)
    {
        int pageCount = pages.Count;
        var urlToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < pageCount; i++)
        {
            urlToIndex[pages[i].Url] = i;
        }

        // Build link graph
        var inboundLinks = new List<HashSet<int>>();
        int[] outboundCounts = new int[pageCount];

        for (int i = 0; i < pageCount; i++)
        {
            inboundLinks.Add([]);
        }

        foreach (SitemapPage page in pages)
        {
            if (page.InternalLinks == null)
                continue;
            int sourceIndex = urlToIndex[page.Url];

            foreach (string? link in page.InternalLinks.Distinct())
            {
                if (urlToIndex.TryGetValue(link, out int targetIndex) && targetIndex != sourceIndex)
                {
                    _ = inboundLinks[targetIndex].Add(sourceIndex);
                    outboundCounts[sourceIndex]++;
                }
            }
        }

        // Initialize PageRank
        double[] pageRank = new double[pageCount];
        double[] newPageRank = new double[pageCount];
        double initialRank = 1.0 / pageCount;

        for (int i = 0; i < pageCount; i++)
        {
            pageRank[i] = initialRank;
        }

        // Iterate to convergence
        for (int iteration = 0; iteration < _options.PageRankIterations; iteration++)
        {
            for (int i = 0; i < pageCount; i++)
            {
                double sum = 0;

                foreach (int sourceIndex in inboundLinks[i])
                {
                    int outCount = outboundCounts[sourceIndex];
                    if (outCount > 0)
                    {
                        sum += pageRank[sourceIndex] / outCount;
                    }
                }

                newPageRank[i] = ((1 - _options.PageRankDampingFactor) / pageCount) + (_options.PageRankDampingFactor * sum);
            }

            (pageRank, newPageRank) = (newPageRank, pageRank);
        }

        // Normalize to 0-100 scale
        double maxRank = pageRank.Max();
        double minRank = pageRank.Min();
        double range = maxRank - minRank;

        for (int i = 0; i < pageCount; i++)
        {
            pages[i].PageRankScore = range > 0 ? (pageRank[i] - minRank) / range * 100 : 50;
        }
    }

    private void CalculateLinkMetrics(List<SitemapPage> pages)
    {
        // Build URL lookup - handle duplicates by taking first occurrence
        var urlToPage = new Dictionary<string, SitemapPage>(StringComparer.OrdinalIgnoreCase);
        foreach (SitemapPage page in pages)
        {
            if (!string.IsNullOrWhiteSpace(page.Url) && !urlToPage.ContainsKey(page.Url))
            {
                urlToPage[page.Url] = page;
            }
        }

        // Count inbound links
        foreach (SitemapPage page in pages)
        {
            if (page.InternalLinks == null)
                continue;

            foreach (string? link in page.InternalLinks.Distinct())
            {
                if (urlToPage.TryGetValue(link, out SitemapPage? targetPage))
                {
                    targetPage.InboundLinkCount++;
                }
            }
        }

        // Normalize scores
        int maxInbound = pages.Max(p => p.InboundLinkCount);
        int maxOutbound = pages.Max(p => p.InternalLinkCount);

        foreach (SitemapPage page in pages)
        {
            page.InboundLinksScore = maxInbound > 0 ? page.InboundLinkCount / (double)maxInbound * 100 : 0;
            page.OutboundLinksScore = maxOutbound > 0 ? page.InternalLinkCount / (double)maxOutbound * 100 : 0;
        }
    }

    private void AnalyzeStructure(List<SitemapPage> pages)
    {
        // For now, use heuristics based on available data
        // In future: parse HTML to count H2, detect TOC, etc.

        foreach (SitemapPage page in pages)
        {
            double score = 0.0;

            // Has H1
            if (!string.IsNullOrEmpty(page.H1))
                score += 30;

            // Has meta description
            if (!string.IsNullOrEmpty(page.MetaDescription))
                score += 20;

            // Has title
            if (!string.IsNullOrEmpty(page.Title))
                score += 20;

            // URL structure (shorter = better for pillars)
            int pathSegments = new Uri(page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            if (pathSegments <= 2)
                score += 30;
            else if (pathSegments <= 3)
                score += 15;

            page.StructureScore = score;
        }
    }

    private void CalculateDepth(List<SitemapPage> pages)
    {
        // URL depth = number of path segments
        int maxDepth = 0;

        foreach (SitemapPage page in pages)
        {
            int pathSegments = new Uri(page.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            page.UrlDepth = pathSegments;
            if (pathSegments > maxDepth)
                maxDepth = pathSegments;
        }

        // Normalize (lower depth = higher score)
        foreach (SitemapPage page in pages)
        {
            page.DepthScore = maxDepth > 0 ? (1.0 - (page.UrlDepth / (double)maxDepth)) * 100 : 100;
        }
    }

    private void ComputePillarScores(List<SitemapPage> pages)
    {
        foreach (SitemapPage page in pages)
        {
            double score = 0.0;

            if (_options.EnablePageRank)
                score += page.PageRankScore * _options.PageRankWeight;

            if (_options.EnableInboundLinks)
                score += page.InboundLinksScore * _options.InboundLinksWeight;

            if (_options.EnableOutboundLinks)
                score += page.OutboundLinksScore * _options.OutboundLinksWeight;

            if (_options.EnableStructure)
                score += page.StructureScore * _options.StructureWeight;

            if (_options.EnableDepth)
                score += page.DepthScore * _options.DepthWeight;

            // Semantic breadth (placeholder)
            if (_options.EnableSemanticBreadth)
                score += 0; // TODO

            page.PillarScore = score;
        }
    }

    private void IdentifyPillarPages(List<SitemapPage> pages)
    {
        double threshold;

        if (_options.UsePercentileThreshold)
        {
            var sorted = pages.OrderBy(p => p.PillarScore).ToList();
            int percentileIndex = (int)(sorted.Count * _options.PillarPercentile);
            threshold = sorted[Math.Min(percentileIndex, sorted.Count - 1)].PillarScore;

            _logger?.LogInformation("Pillar threshold (p{Percentile}): {Threshold:F2}", _options.PillarPercentile * 100, threshold);
        }
        else
        {
            threshold = _options.PillarScoreThreshold;
        }

        int pillarCount = 0;
        foreach (SitemapPage page in pages)
        {
            page.IsPillarCandidate = page.PillarScore >= threshold;
            if (page.IsPillarCandidate)
                pillarCount++;
        }

        _logger?.LogInformation("Identified {PillarCount} pillar candidates (threshold: {Threshold:F2})", pillarCount, threshold);
    }
}
