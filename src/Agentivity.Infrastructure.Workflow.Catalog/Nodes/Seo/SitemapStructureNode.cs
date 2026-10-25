using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;
using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo;

/// <summary>
/// Sitemap Structure Node - analyzes pages and creates hierarchical content structure (pillars, clusters)
/// </summary>
public sealed class SitemapStructureNode : BaseAINode
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SitemapStructureNode> _logger;


    public SitemapStructureNode(ILlmClientFactory llmClientFactory, IHttpClientFactory httpClientFactory, ILogger<SitemapStructureNode> logger)
        : base("seo.sitemap.structure", llmClientFactory, logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Sitemap Structure", "Analyzes sitemap pages using LLM to create hierarchical content structure (pillars & clusters).", "seo", whenToUse: "To group a site's pages into main topics (pillars) and topic clusters with an AI, from the page list Sitemap Analyzer gives. Its result feeds Internal Link Suggester.");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.Costs);

        DefineResourceInputPort("llm_model", "LLM Model", ResourcePortCategory.LlmModel, "LLM model for semantic analysis");
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "The embeddings model that compares the pages by meaning to group them (Local Embeddings is free).");
        DefineResourceInputPort("scraper", "Scraper", ResourcePortCategory.WebScraper, "Web scraper for content analysis (optional)", isRequired: false);

        DefineInput(
            "pages",
            ArgumentType.Text,
            "Sitemap pages (JSON array), as the Sitemap Analyzer step gives it",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Source",
                Order = 10,
                Multiline = true,
                Rows = 10,
                Format = "json",
            },
            required: true
        );
        DefineInput(
            "site_name",
            ArgumentType.Text,
            "Website name/brand",
            "Website",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Source",
                Order = 20,
            },
            required: false
        );
        DefineInput(
            "max_pillars",
            ArgumentType.Number,
            "Maximum content pillars to identify",
            10,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Structure",
                Order = 30,
            },
            required: false
        );
        DefineInput(
            "scrape_pages",
            ArgumentType.Boolean,
            "Scrape pages to analyze content and internal links (slower but more accurate)",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Enrichment",
                Order = 40,
            },
            required: false
        );
        DefineInput(
            "max_pages_to_scrape",
            ArgumentType.Number,
            "Maximum pages to scrape for analysis (0 = all)",
            50,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Enrichment",
                Order = 50,
            },
            required: false
        );

        // ✅ Topical Clustering algorithm parameters (improved algorithm)
        DefineInput(
            "max_topics_per_page",
            ArgumentType.Number,
            "Maximum topics to extract per page",
            8,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Clustering",
                Order = 60,
            },
            required: false
        );
        DefineInput(
            "min_topic_similarity",
            ArgumentType.Number,
            "Minimum similarity threshold for topic edges (0.0-1.0)",
            0.3,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Clustering",
                Order = 70,
            },
            required: false
        );
        DefineInput(
            "max_topics_after_reduction",
            ArgumentType.Number,
            "Maximum topics after set-cover reduction",
            100,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Clustering",
                Order = 80,
            },
            required: false
        );
        DefineInput(
            "cluster_cohesion_threshold",
            ArgumentType.Number,
            "λ₂ threshold for cluster cohesion (0.0-1.0)",
            0.5,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Clustering",
                Order = 90,
            },
            required: false
        );
        DefineInput(
            "min_cluster_size",
            ArgumentType.Number,
            "Minimum cluster size",
            2,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Clustering",
                Order = 100,
            },
            required: false
        );

        // ✅ Anti-leakage parameters (margin rule)
        DefineInput(
            "assignment_margin",
            ArgumentType.Number,
            "Minimum gap between best/2nd cluster for assignment (0.0-0.3)",
            0.1,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Assignment",
                Order = 110,
            },
            required: false
        );
        DefineInput(
            "min_child_support",
            ArgumentType.Number,
            "Minimum fraction of cluster pages pillar must link to (0.0-1.0)",
            0.1,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Assignment",
                Order = 120,
            },
            required: false
        );

        DefineOutput("site_structure", ArgumentType.Text, "Site structure with pillars, topic clusters, and orphan pages (JSON)");
        DefineOutput("total_pillars", ArgumentType.Number, "Number of content pillars identified");
        DefineOutput("total_clusters", ArgumentType.Number, "Number of topic clusters identified");

        DefineIcon(NodeIcon.Embedded("sitemap_structure_node"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Structuring sitemap into content pillars and clusters...");

        ArgumentInputValues args = GetInputs(config);
        string siteName = args.GetText("site_name") ?? "Website";

        // Get pages as JSON string and parse to JsonElement for internal processing
        string pagesJson = args.RequireText("pages");
        JsonElement pagesElement = JsonDocument.Parse(pagesJson).RootElement;

        // Convert to internal working list
        List<SitemapPage> pages = ParsePagesFromJson(pagesElement);

        if (pages.Count == 0)
        {
            throw new InvalidOperationException("No pages to analyze");
        }

        // ✅ FIX: Extract DOMAIN from first page URL (not siteName which is a brand name)
        string siteDomain = ExtractDomainFromUrl(pages.First().Url);
        if (string.IsNullOrEmpty(siteDomain))
        {
            throw new InvalidOperationException($"Could not extract domain from URL: {pages.First().Url}");
        }

        services.PublishStatus($"Analyzing {pages.Count} pages to identify content structure...");
        services.PublishStatus($"Site domain: {siteDomain}");

        // Optionally enrich pages with scraping
        bool scrapePages = args.GetBoolean("scrape_pages") ?? true;
        int maxPagesToScrape = args.GetInt("max_pages_to_scrape") ?? 50;

        if (scrapePages)
        {
            pages = await EnrichPagesWithScrapingAsync(config, services, pages, maxPagesToScrape, siteDomain, cancellationToken);
        }

        // Log content statistics for debugging
        int pagesWithTitle = pages.Count(p => !string.IsNullOrEmpty(p.Title));
        int pagesWithH1 = pages.Count(p => !string.IsNullOrEmpty(p.H1));
        int pagesWithMeta = pages.Count(p => !string.IsNullOrEmpty(p.MetaDescription));
        int pagesWithExcerpt = pages.Count(p => !string.IsNullOrEmpty(p.Excerpt));
        int pagesWithLinks = pages.Count(p => p.InternalLinkCount > 0);
        int noindexPages = pages.Count(p => !p.IsIndexable);

        services.PublishStatus($"📊 Content stats after enrichment:");
        services.PublishStatus($"   - {pagesWithTitle}/{pages.Count} have title");
        services.PublishStatus($"   - {pagesWithH1}/{pages.Count} have H1");
        services.PublishStatus($"   - {pagesWithMeta}/{pages.Count} have meta description");
        services.PublishStatus($"   - {pagesWithExcerpt}/{pages.Count} have excerpt");
        services.PublishStatus($"   - {pagesWithLinks}/{pages.Count} have internal links");
        services.PublishStatus($"   - {noindexPages}/{pages.Count} marked as noindex");

        // Filter out noindex pages
        if (noindexPages > 0)
        {
            services.PublishStatus($"🚫 Excluding {noindexPages} noindex pages from structure");
            pages = pages.Where(p => p.IsIndexable).ToList();
        }

        // Build options from inputs
        var clusteringOptions = new TopicalClusteringOptions
        {
            MaxTopicsPerPage = args.GetInt("max_topics_per_page") ?? 8,
            MinTopicSimilarity = args.GetDouble("min_topic_similarity") ?? 0.3,
            MaxTopicsAfterReduction = args.GetInt("max_topics_after_reduction") ?? 100,
            ClusterCohesionThreshold = args.GetDouble("cluster_cohesion_threshold") ?? 0.5,
            MinClusterSize = args.GetInt("min_cluster_size") ?? 2,
            AssignmentMargin = args.GetDouble("assignment_margin") ?? 0.1,
            MinChildSupportForPillar = args.GetDouble("min_child_support") ?? 0.1,
        };

        // Execute clustering (requires LLM + RAG, no fallback)
        SiteStructure structure = await ClusterWithTopicalAlgorithmAsync(config, services, pages, siteName, clusteringOptions, cancellationToken);

        int totalClusters = structure.TopicClusters.Count;

        services.PublishStatus($"✅ Structure complete: {structure.Pillars.Count} pillars, {totalClusters} topic clusters, {structure.OrphanPages.Count} orphans");

        if (structure.Pillars.Count == 0)
        {
            _logger.LogWarning("Warning: No pillars identified! Structure is empty.");
            services.PublishStatus("⚠️ WARNING: No content pillars were identified. Check if the site has enough content.");
        }

        return NodeResult.Create().AddText("site_structure", JsonSerializer.Serialize(structure)).AddNumber("total_pillars", structure.Pillars.Count).AddNumber("total_clusters", totalClusters);
    }

    /// <summary>
    /// Main clustering method using the Topical Clustering algorithm.
    /// Requires LLM + RAG - no fallback.
    /// </summary>
    private async Task<SiteStructure> ClusterWithTopicalAlgorithmAsync(NodeConfig config, WorkflowRunServices services, List<SitemapPage> pages, string siteName, TopicalClusteringOptions options, CancellationToken cancellationToken)
    {
        services.PublishStatus("📊 Pre-processing: Calculating PageRank and link metrics...");

        var detector = new PillarPageDetector(
            new PillarPageDetectorOptions
            {
                EnablePageRank = true,
                EnableInboundLinks = true,
                EnableOutboundLinks = true,
                EnableStructure = false,
                EnableDepth = false,
                UsePercentileThreshold = false,
            },
            _logger
        );
        detector.AnalyzePages(pages);

        var topByPageRank = pages.OrderByDescending(p => p.PageRankScore).Take(5).ToList();
        services.PublishStatus($"   Top 5 by PageRank:");
        foreach (SitemapPage? p in topByPageRank)
        {
            services.PublishStatus($"      - {p.Title ?? p.Url} (PR: {p.PageRankScore:F2}, Inbound: {p.InboundLinkCount})");
        }

        // Get embedding provider (REQUIRED)
        (IEmbeddingProvider Tool, ResourceBinding Binding)? embeddingResolved = ResourceResolver.TryResolveToolNode<IEmbeddingProvider>(config, "embeddings", ResourcePortCategory.Embeddings);

        if (embeddingResolved is null)
        {
            throw new InvalidOperationException("Connect an Embeddings step (Local Embeddings is free) to the Embeddings port: it compares the pages by meaning.");
        }

        (IEmbeddingProvider? embeddingProvider, ResourceBinding? embeddingBinding) = embeddingResolved.Value;

        services.PublishStatus($"Using embedding provider: {embeddingProvider.GetModelName(embeddingBinding)} (dim: {embeddingProvider.GetEmbeddingDimensions(embeddingBinding)})");

        // Get LLM provider (REQUIRED)
        (ILlmProvider? llmProvider, ResourceBinding? llmBinding) = ResourceResolver.RequireToolNode<ILlmProvider>(config, "llm_model", ResourcePortCategory.LlmModel);

        services.PublishStatus("🔬 Using Topical Clustering algorithm...");

        var orchestrator = new TopicalClusteringOrchestrator(_logger, options);

        TopicalClusteringResult result = await orchestrator.ExecuteAsync(pages, llmProvider, llmBinding, embeddingProvider, embeddingBinding, siteName, msg => services.PublishStatus(msg), cancellationToken);

        if (!result.Success || result.Structure == null)
        {
            throw new InvalidOperationException($"Topical clustering failed: {result.Error ?? "Unknown error"}");
        }

        services.PublishStatus($"📋 Clustering stats: {result.TotalTopicsExtracted} topics extracted, " + $"{result.TopicsAfterReduction} after reduction, {result.TopicClustersCreated} clusters");

        return result.Structure;
    }

    private async ValueTask<List<SitemapPage>> EnrichPagesWithScrapingAsync(NodeConfig config, WorkflowRunServices services, List<SitemapPage> pages, int maxPagesToScrape, string siteDomain, CancellationToken cancellationToken)
    {
        ResourceBinding? scraperBinding = ResourceResolver.TryResolve(config, "scraper", "web.scraper.*");
        if (scraperBinding == null)
        {
            _logger.LogWarning("No scraper connected, skipping page enrichment");
            return pages;
        }

        // Limit pages to scrape (prioritize by priority field if available)
        List<SitemapPage> pagesToScrape = maxPagesToScrape > 0 ? pages.OrderByDescending(p => p.Priority ?? 0).Take(maxPagesToScrape).ToList() : pages;

        services.PublishStatus($"Scraping {pagesToScrape.Count} pages for content analysis...");

        // Create scraper based on binding (simplified - assumes DirectScraper)
        HttpClient httpClient = _httpClientFactory.CreateClient();
        var scraper = new Infrastructure.Web.Scrapers.DirectWebScraper(httpClient, _logger);

        foreach (SitemapPage page in pagesToScrape)
        {
            try
            {
                _logger.LogInformation("📥 Scraping: {Url}", page.Url);

                ScrapeResult result = await scraper.ScrapeAsync(
                    page.Url,
                    new Infrastructure.Web.ScrapeOptions
                    {
                        ExtractMetadata = true,
                        TimeoutSeconds = 10,
                        MaxContentLength = 500000, // ✅ 500 KB instead of 50 KB to capture all internal links
                    },
                    cancellationToken
                );

                _logger.LogInformation("   Scrape result: Success={Success}, HasMetadata={HasMeta}", result.Success, result.Metadata != null);

                if (result.Success && result.Metadata != null)
                {
                    page.MetaDescription = result.Metadata.GetValueOrDefault("meta_description");
                    page.H1 = result.Metadata.GetValueOrDefault("h1");
                    page.Excerpt = result.Metadata.GetValueOrDefault("excerpt");

                    // ✅ Detect noindex from meta robots
                    page.MetaRobots = result.Metadata.GetValueOrDefault("robots");
                    if (!string.IsNullOrEmpty(page.MetaRobots))
                    {
                        page.IsIndexable = !page.MetaRobots.Contains("noindex", StringComparison.OrdinalIgnoreCase);
                        if (!page.IsIndexable)
                        {
                            _logger.LogInformation("   🚫 Page marked as noindex: {Url}", page.Url);
                        }
                    }

                    // ✅ DEBUG: Log link extraction results
                    int rawLinksCount = result.InternalLinks?.Count ?? 0;

                    _logger.LogInformation("   Links found: {Internal} internal", rawLinksCount);

                    if (rawLinksCount == 0)
                    {
                        _logger.LogWarning("   ⚠️ NO internal links found by scraper for {Url}", page.Url);
                    }
                    else
                    {
                        // Log first 10 raw links
                        _logger.LogInformation("   Raw internal links:");
                        foreach (string? link in result.InternalLinks!.Take(10))
                        {
                            _logger.LogInformation("      - {Link}", link);
                        }
                        if (rawLinksCount > 10)
                        {
                            _logger.LogInformation("      ... and {More} more", rawLinksCount - 10);
                        }
                    }

                    // ✅ FILTER external URLs + junk URLs + self-links
                    _logger.LogInformation("   Filtering with siteDomain={Domain}, currentPage={Page}", siteDomain, page.Url);

                    page.InternalLinks = UrlFilter.FilterInternalUrls(result.InternalLinks, siteDomain, page.Url);

                    int filteredCount = page.InternalLinkCount;
                    _logger.LogInformation("   After filtering: {FilteredCount} links kept (removed: {Removed})", filteredCount, rawLinksCount - filteredCount);

                    if (filteredCount == 0 && rawLinksCount > 0)
                    {
                        _logger.LogWarning("   ⚠️ ALL {Count} links were filtered out!", rawLinksCount);
                    }

                    if (filteredCount > 0)
                    {
                        // Log filtered links that were kept
                        _logger.LogInformation("   Kept links:");
                        foreach (string? link in page.InternalLinks!.Take(5))
                        {
                            _logger.LogInformation("      ✅ {Link}", link);
                        }
                        if (filteredCount > 5)
                        {
                            _logger.LogInformation("      ... and {More} more", filteredCount - 5);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("   Scraping failed or no metadata for {Url}", page.Url);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scrape {Url}", page.Url);
            }

            // Small delay to avoid rate limiting
            await Task.Delay(100, cancellationToken);
        }

        return pages;
    }

    /// <summary>
    /// Extract keywords from page titles and URLs
    /// </summary>
    private static List<string> ExtractKeywordsFromPages(SitemapPage pillarPage, List<SitemapPage> subPages)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(pillarPage.Title))
        {
            foreach (string word in pillarPage.Title.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4 && !IsStopWord(word))
                {
                    _ = keywords.Add(word.ToLowerInvariant());
                }
            }
        }

        string topic = ExtractTopicFromUrl(pillarPage.Url);
        if (!string.IsNullOrEmpty(topic))
        {
            foreach (string word in topic.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 3 && !IsStopWord(word))
                {
                    _ = keywords.Add(word.ToLowerInvariant());
                }
            }
        }

        return keywords.Take(7).ToList();
    }

    private static bool IsStopWord(string word)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the",
            "and",
            "for",
            "with",
            "from",
            "this",
            "that",
            "have",
            "has",
            "had",
            "are",
            "was",
            "were",
            "will",
            "can",
            "could",
            "would",
            "should",
            "may",
            "your",
            "our",
            "their",
            "into",
            "about",
            "more",
            "when",
            "where",
            "what",
        };

        return stopWords.Contains(word);
    }

    private static string ExtractTopicFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath.Trim('/');
            string[] parts = path.Split('/');
            string lastPart = parts.LastOrDefault() ?? "content";
            return lastPart.Replace('-', ' ').Replace('_', ' ');
        }
        catch
        {
            return "Content";
        }
    }

    /// <summary>
    /// Parses pages from JsonElement input. Internal conversion from generic JSON to typed list.
    /// </summary>
    private static List<SitemapPage> ParsePagesFromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected 'pages' to be a JSON array.");
        }

        var pages = new List<SitemapPage>();
        foreach (JsonElement pageElement in element.EnumerateArray())
        {
            var page = new SitemapPage
            {
                // ✅ FIX: Use PascalCase property names (default JSON serialization)
                Url = pageElement.TryGetProperty("Url", out JsonElement url) ? url.GetString() ?? "" : "",
                Title = pageElement.TryGetProperty("Title", out JsonElement title) ? title.GetString() : null,
                H1 = pageElement.TryGetProperty("H1", out JsonElement h1) ? h1.GetString() : null,
                MetaDescription = pageElement.TryGetProperty("MetaDescription", out JsonElement meta) ? meta.GetString() : null,
                Excerpt = pageElement.TryGetProperty("Excerpt", out JsonElement excerpt) ? excerpt.GetString() : null,
                LastModified = pageElement.TryGetProperty("LastModified", out JsonElement lastMod) && lastMod.ValueKind != JsonValueKind.Null ? lastMod.GetDateTime() : null,
                ChangeFrequency = pageElement.TryGetProperty("ChangeFrequency", out JsonElement freq) ? freq.GetString() : null,
                Priority = pageElement.TryGetProperty("Priority", out JsonElement prio) && prio.ValueKind != JsonValueKind.Null ? prio.GetDecimal() : null,
                InternalLinks = pageElement.TryGetProperty("InternalLinks", out JsonElement intLinks) && intLinks.ValueKind == JsonValueKind.Array ? intLinks.EnumerateArray().Select(l => l.GetString() ?? "").ToList() : null,
                ExternalLinks = pageElement.TryGetProperty("ExternalLinks", out JsonElement extLinks) && extLinks.ValueKind == JsonValueKind.Array ? extLinks.EnumerateArray().Select(l => l.GetString() ?? "").ToList() : null,
                IsIndexable = !pageElement.TryGetProperty("IsIndexable", out JsonElement indexable) || indexable.ValueKind == JsonValueKind.Null || indexable.GetBoolean(),
            };
            pages.Add(page);
        }
        return pages;
    }

    /// <summary>
    /// Extracts the domain (host) from a URL.
    /// Example: "https://www.example.com/page" -> "example.com"
    /// </summary>
    private static string ExtractDomainFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();

            // Remove "www." prefix if present
            if (host.StartsWith("www."))
            {
                host = host.Substring(4);
            }

            return host;
        }
        catch
        {
            return string.Empty;
        }
    }
}
