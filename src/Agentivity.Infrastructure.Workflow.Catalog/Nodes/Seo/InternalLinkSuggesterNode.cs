using System.Text;
using System.Text.Json;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;
using Agentivity.Infrastructure.Workflow.Catalog.Seo;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo;

/// <summary>
/// Internal Link Suggester Node - suggests relevant internal links for an article based on site structure.
/// Uses a STRUCTURE-FIRST approach with quotas per category, then semantic scoring for ranking.
/// </summary>
public sealed class InternalLinkSuggesterNode : BaseAINode
{
    private readonly ILogger<InternalLinkSuggesterNode> _logger;

    private const int EmbeddingBatchSize = 20;
    private const int LlmBatchSize = 10;
    private const double MinSemanticScore = 20.0; // Minimum score to be considered relevant

    // URL patterns to exclude (non-content pages)
    private static readonly string[] JunkUrlPatterns =
    [
        // Navigation / utility pages
        "/press",
        "/about",
        "/privacy",
        "/terms",
        "/contact",
        "/legal",
        "/cookies",
        "/sitemap",
        "/search",
        "/login",
        "/register",
        "/cart",
        "/checkout",
        "/account",
        "/admin",
        "/wp-admin",
        "/feed",
        // Taxonomy / archive pages (lists, not content)
        "/category/",
        "/tag/",
        "/author/",
        "/page/",
        "/attachment/",
        "/topics/",
        "/archive/",
        "/archives/",
        // Commercial pages
        "/affiliates",
        "/affiliate",
        "/partners",
        "/pricing",
        "/plans",
        "/subscribe",
        "/newsletter",
        "/meet-the",
        "/team",
        "/careers",
        // WordPress specific
        "/wp-content/",
        "/wp-includes/",
        "/wp-json/",
    ];

    // Title patterns that indicate non-content pages
    private static readonly string[] JunkTitlePatterns =
    [
        "- Flows Trading", // Category pages often have this format
        "Archives",
        "Category:",
        "Tag:",
        "Author:",
    ];

    private const string AnchorSchemaJson = """
        {
            "type": "object",
            "properties": {
                "anchors": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "id": { "type": "integer" },
                            "anchor_text": { "type": "string" },
                            "placement_hint": { "type": "string" }
                        },
                        "required": ["id", "anchor_text"]
                    }
                }
            },
            "required": ["anchors"]
        }
        """;

    public InternalLinkSuggesterNode(ILlmClientFactory llmClientFactory, ILogger<InternalLinkSuggesterNode> logger)
        : base("seo.links.suggest", llmClientFactory, logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Internal Link Suggester", "Suggests relevant internal links using structure-first selection with semantic scoring.", "seo");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("llm_model", "LLM Model", ResourcePortCategory.LlmModel, "LLM for anchor text generation");
        DefineResourceInputPort("llm_memory", "RAG/Memory", ResourcePortCategory.LlmMemory, "Embeddings for semantic scoring (optional)", isRequired: false);

        DefineInput(
            "article_topic",
            ArgumentType.Text,
            "Article topic or title",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Article",
                Order = 10,
            }
        );
        DefineInput(
            "article_summary",
            ArgumentType.Text,
            "Brief article summary/outline (optional)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Article",
                Order = 20,
                Multiline = true,
                Rows = 5,
            }
        );
        DefineInput(
            "site_structure",
            ArgumentType.Text,
            "Site structure JSON (from SitemapStructureNode)",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Structure",
                Order = 30,
                Multiline = true,
                Rows = 10,
                Format = "json",
            }
        );
        DefineInput(
            "max_links",
            ArgumentType.Number,
            "Maximum links to suggest",
            10,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Limits",
                Order = 40,
            }
        );
        DefineInput(
            "parent_pillar_url",
            ArgumentType.Text,
            "Parent pillar URL (required for best results)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "config",
                Order = 50,
            }
        );

        // Quota configuration
        DefineInput(
            "quota_parent_pillar",
            ArgumentType.Number,
            "Links to parent pillar",
            1,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Quotas",
                Order = 60,
            }
        );
        DefineInput(
            "quota_sibling_pillars",
            ArgumentType.Number,
            "Links to sibling pillars",
            2,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Quotas",
                Order = 70,
            }
        );
        DefineInput(
            "quota_same_pillar_leaves",
            ArgumentType.Number,
            "Links to leaves in same pillar",
            4,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Quotas",
                Order = 80,
            }
        );
        DefineInput(
            "quota_other_pillars",
            ArgumentType.Number,
            "Links to other relevant pillars",
            2,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Quotas",
                Order = 90,
            }
        );
        DefineInput(
            "quota_cross_pillar_leaves",
            ArgumentType.Number,
            "Links to leaves in other pillars",
            1,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Quotas",
                Order = 100,
            }
        );

        DefineOutput("suggested_links", ArgumentType.Text, "List of suggested links (JSON array)");
        DefineOutput("total_suggestions", ArgumentType.Number, "Number of links suggested");

        DefineIcon(NodeIcon.Embedded("internal_link_suggester_node"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Suggesting internal links (structure-first approach)...");

        ArgumentInputValues args = GetInputs(config);
        string articleTopic = args.RequireText("article_topic");
        string articleSummary = args.GetText("article_summary") ?? "";
        int maxLinks = args.GetInt("max_links") ?? 10;
        string parentPillarUrl = args.GetText("parent_pillar_url") ?? "";

        // Quotas
        var quotas = new LinkQuotas
        {
            ParentPillar = args.GetInt("quota_parent_pillar") ?? 1,
            SiblingPillars = args.GetInt("quota_sibling_pillars") ?? 2,
            SamePillarLeaves = args.GetInt("quota_same_pillar_leaves") ?? 4,
            OtherPillars = args.GetInt("quota_other_pillars") ?? 2,
            CrossPillarLeaves = args.GetInt("quota_cross_pillar_leaves") ?? 1,
        };

        services.PublishStatus($"Topic: {articleTopic}");
        services.PublishStatus($"Parent pillar: {(string.IsNullOrEmpty(parentPillarUrl) ? "(not specified)" : parentPillarUrl)}");

        // Get site structure as JSON string and parse to JsonElement for internal processing
        string structureJson = args.RequireText("site_structure");
        JsonElement structureElement = JsonDocument.Parse(structureJson).RootElement;

        // Convert to internal working structure
        SiteStructure structure = ParseStructureFromJson(structureElement);

        services.PublishStatus($"📊 Structure: {structure.Pillars.Count} pillars, {structure.OrphanPages.Count} orphans");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 1: Find or infer the best parent pillar
        // ═══════════════════════════════════════════════════════════════════════
        ContentPillar? parentPillar = null;

        if (!string.IsNullOrEmpty(parentPillarUrl))
        {
            parentPillar = FindPillarByUrl(structure, parentPillarUrl);
            if (parentPillar != null)
            {
                services.PublishStatus($"✅ Found specified parent pillar: {parentPillar.PillarPage.Title}");
            }
            else
            {
                // Provide detailed diagnostic info
                List<string> allPillarUrls = GetAllPillarUrls(structure);
                services.PublishStatus($"⚠️ Parent pillar URL not found in structure: {parentPillarUrl}");
                services.PublishStatus($"   This URL is not a pillar page. It may be a leaf page or not in the structure.");
                services.PublishStatus($"   Available pillars ({allPillarUrls.Count}):");
                foreach (string? url in allPillarUrls.Take(5))
                {
                    services.PublishStatus($"      - {url}");
                }
                if (allPillarUrls.Count > 5)
                {
                    services.PublishStatus($"      ... and {allPillarUrls.Count - 5} more");
                }
            }
        }

        // If no parent specified or not found, infer the best one from the topic
        if (parentPillar == null)
        {
            parentPillar = await InferBestPillarAsync(structure, articleTopic, articleSummary, config, services, cancellationToken);
            if (parentPillar != null)
            {
                services.PublishStatus($"🔍 Inferred parent pillar: {parentPillar.PillarPage.Title}");
            }
        }

        CategorizedPages categorizedPages = BuildCategorizedPageSets(structure, parentPillar);

        services.PublishStatus($"Categorized pages:");
        services.PublishStatus($"   • Parent pillar: {categorizedPages.ParentPillarPage?.Title ?? "N/A"}");
        services.PublishStatus($"   • Sibling pillars: {categorizedPages.SiblingPillars.Count}");
        services.PublishStatus($"   • Same pillar leaves: {categorizedPages.SamePillarLeaves.Count}");
        services.PublishStatus($"   • Other pillars: {categorizedPages.OtherPillars.Count}");
        services.PublishStatus($"   • Cross-pillar leaves: {categorizedPages.CrossPillarLeaves.Count}");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 2: Compute semantic scores for each category
        // ═══════════════════════════════════════════════════════════════════════
        (IEmbeddingProvider Tool, ResourceBinding Binding)? embeddingResolved = ResourceResolver.TryResolveToolNode<IEmbeddingProvider>(config, "llm_memory", ResourcePortCategory.LlmMemory);

        IEmbeddingCapability? embeddingProvider = embeddingResolved is not null ? new EmbeddingProviderAdapter(embeddingResolved.Value.Tool, embeddingResolved.Value.Binding) : null;

        var allCandidates = new List<LinkCandidate>();

        // Add parent pillar (always include if exists)
        if (categorizedPages.ParentPillarPage != null)
        {
            allCandidates.Add(
                new LinkCandidate
                {
                    Page = categorizedPages.ParentPillarPage,
                    Category = "parent-pillar",
                    SemanticScore = 100, // Always max score for parent
                    StructuralScore = 50,
                }
            );
        }

        // Process each category
        await ScoreCandidatesAsync(categorizedPages.SiblingPillars, "sibling-pillar", articleTopic, articleSummary, embeddingProvider, allCandidates, config, services, cancellationToken);

        await ScoreCandidatesAsync(categorizedPages.SamePillarLeaves, "same-pillar-leaf", articleTopic, articleSummary, embeddingProvider, allCandidates, config, services, cancellationToken);

        await ScoreCandidatesAsync(categorizedPages.OtherPillars, "other-pillar", articleTopic, articleSummary, embeddingProvider, allCandidates, config, services, cancellationToken);

        await ScoreCandidatesAsync(categorizedPages.CrossPillarLeaves, "cross-pillar-leaf", articleTopic, articleSummary, embeddingProvider, allCandidates, config, services, cancellationToken);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 3: Select links by quota (structure-first)
        // ═══════════════════════════════════════════════════════════════════════
        List<LinkCandidate> selectedLinks = SelectByQuota(allCandidates, quotas, maxLinks, config, services);

        services.PublishStatus($"Selected {selectedLinks.Count} links by quota");

        // Assign IDs for anchor generation
        for (int i = 0; i < selectedLinks.Count; i++)
        {
            selectedLinks[i].Id = i;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 4: Generate anchor texts with LLM
        // ═══════════════════════════════════════════════════════════════════════
        (ILlmProvider? llm, ResourceBinding? llmBinding) = ResourceResolver.RequireToolNode<ILlmProvider>(config, "llm_model", ResourcePortCategory.LlmModel);
        await GenerateAnchorTextsAsync(llm, llmBinding, articleTopic, articleSummary, selectedLinks, config, services, cancellationToken);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 5: Build output
        // ═══════════════════════════════════════════════════════════════════════
        var topLinks = selectedLinks
            .Select(c => new InternalLinkSuggestion
            {
                TargetUrl = c.Page.Url,
                TargetTitle = c.Page.Title ?? ExtractTitleFromUrl(c.Page.Url),
                SuggestedAnchorText = c.SuggestedAnchor ?? c.Page.Title ?? ExtractTitleFromUrl(c.Page.Url),
                Relevance = ScoreToRelevance(c.FinalScore),
                Reason = $"{c.Category} | sem:{c.SemanticScore:F0}",
                ContextSuggestion = c.PlacementHint,
                Category = c.Category,
                CategoryWeight = c.FinalScore,
            })
            .ToList();

        services.PublishStatus($"✅ Final selection ({topLinks.Count} links):");
        foreach (InternalLinkSuggestion? link in topLinks)
        {
            services.PublishStatus($"   • [{link.Category}] {link.TargetTitle}");
            services.PublishStatus($"     → \"{link.SuggestedAnchorText}\"");
        }

        return NodeResult.Create().AddText("suggested_links", JsonSerializer.Serialize(topLinks)).AddNumber("total_suggestions", topLinks.Count);
    }

    /// <summary>
    /// Build categorized page sets based on structure
    /// </summary>
    private CategorizedPages BuildCategorizedPageSets(SiteStructure structure, ContentPillar? parentPillar)
    {
        var result = new CategorizedPages();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (parentPillar != null)
        {
            // Parent pillar page
            result.ParentPillarPage = parentPillar.PillarPage;
            _ = seenUrls.Add(parentPillar.PillarPage.Url);

            // Find parent of parent (for siblings)
            ContentPillar? grandParent = FindParentPillar(structure, parentPillar);

            if (grandParent != null)
            {
                // Sibling pillars = other sub-pillars of the same parent
                foreach (ContentPillar sibling in grandParent.SubPillars)
                {
                    if (!sibling.PillarPage.Url.Equals(parentPillar.PillarPage.Url, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsContentPage(sibling.PillarPage) && seenUrls.Add(sibling.PillarPage.Url))
                        {
                            result.SiblingPillars.Add(sibling.PillarPage);
                        }
                    }
                }
            }
            else
            {
                // Parent is a top-level pillar, siblings are other top-level pillars
                foreach (ContentPillar pillar in structure.Pillars)
                {
                    if (!pillar.PillarPage.Url.Equals(parentPillar.PillarPage.Url, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsContentPage(pillar.PillarPage) && seenUrls.Add(pillar.PillarPage.Url))
                        {
                            result.SiblingPillars.Add(pillar.PillarPage);
                        }
                    }
                }
            }

            // Same pillar leaves = leaves directly under parent pillar + its sub-pillars
            CollectLeavesFromPillar(parentPillar, result.SamePillarLeaves, seenUrls);
        }

        // Other pillars = all pillar pages not already included
        foreach (ContentPillar pillar in structure.Pillars)
        {
            if (IsContentPage(pillar.PillarPage) && seenUrls.Add(pillar.PillarPage.Url))
            {
                result.OtherPillars.Add(pillar.PillarPage);
            }
            // Also add sub-pillars
            foreach (ContentPillar subPillar in pillar.SubPillars)
            {
                if (IsContentPage(subPillar.PillarPage) && seenUrls.Add(subPillar.PillarPage.Url))
                {
                    result.OtherPillars.Add(subPillar.PillarPage);
                }
            }
        }

        // Cross-pillar leaves = leaves from other pillars
        foreach (ContentPillar pillar in structure.Pillars)
        {
            if (parentPillar == null || !pillar.PillarPage.Url.Equals(parentPillar.PillarPage.Url, StringComparison.OrdinalIgnoreCase))
            {
                CollectLeavesFromPillar(pillar, result.CrossPillarLeaves, seenUrls);
            }
        }

        return result;
    }

    private static void CollectLeavesFromPillar(ContentPillar pillar, List<SitemapPage> target, HashSet<string> seenUrls)
    {
        foreach (SitemapPage leaf in pillar.LeafPages ?? [])
        {
            if (IsContentPage(leaf) && seenUrls.Add(leaf.Url))
            {
                target.Add(leaf);
            }
        }
        foreach (ContentPillar subPillar in pillar.SubPillars ?? [])
        {
            CollectLeavesFromPillar(subPillar, target, seenUrls);
        }
    }

    /// <summary>
    /// Score candidates using embeddings or keywords
    /// </summary>
    private async Task ScoreCandidatesAsync(List<SitemapPage> pages, string category, string articleTopic, string articleSummary, IEmbeddingCapability? embeddingProvider, List<LinkCandidate> allCandidates, NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken)
    {
        if (pages.Count == 0)
            return;

        var candidates = pages
            .Select(p => new LinkCandidate
            {
                Page = p,
                Category = category,
                StructuralScore = ComputeStructuralScore(p, category),
            })
            .ToList();

        if (embeddingProvider != null)
        {
            await ComputeSemanticScoresWithEmbeddingsAsync(embeddingProvider, articleTopic, articleSummary, candidates, cancellationToken);
        }
        else
        {
            ComputeSemanticScoresWithKeywords(articleTopic, articleSummary, candidates);
        }

        // Compute final score and add to all candidates
        foreach (LinkCandidate? c in candidates)
        {
            c.FinalScore = (c.SemanticScore * 0.7) + (c.StructuralScore * 0.3);
            allCandidates.Add(c);
        }
    }

    /// <summary>
    /// Select links by quota, ensuring structure is respected
    /// </summary>
    private List<LinkCandidate> SelectByQuota(List<LinkCandidate> allCandidates, LinkQuotas quotas, int maxLinks, NodeConfig config, WorkflowRunServices services)
    {
        var selected = new List<LinkCandidate>();
        var usedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Helper to select top N from a category
        void SelectFromCategory(string category, int quota, string label)
        {
            if (quota <= 0)
                return;

            var available = allCandidates.Where(c => c.Category == category && !usedUrls.Contains(c.Page.Url)).Where(c => c.SemanticScore >= MinSemanticScore || category == "parent-pillar").OrderByDescending(c => c.FinalScore).Take(quota).ToList();

            foreach (LinkCandidate? c in available)
            {
                selected.Add(c);
                _ = usedUrls.Add(c.Page.Url);
            }

            services.PublishStatus($"   {label}: selected {available.Count}/{quota}");
        }

        // Select by quota in priority order
        SelectFromCategory("parent-pillar", quotas.ParentPillar, "Parent pillar");
        SelectFromCategory("sibling-pillar", quotas.SiblingPillars, "Sibling pillars");
        SelectFromCategory("same-pillar-leaf", quotas.SamePillarLeaves, "Same pillar leaves");
        SelectFromCategory("other-pillar", quotas.OtherPillars, "Other pillars");
        SelectFromCategory("cross-pillar-leaf", quotas.CrossPillarLeaves, "Cross-pillar leaves");

        // If we have room, fill with best remaining candidates
        if (selected.Count < maxLinks)
        {
            IEnumerable<LinkCandidate> remaining = allCandidates.Where(c => !usedUrls.Contains(c.Page.Url)).Where(c => c.SemanticScore >= MinSemanticScore).OrderByDescending(c => c.FinalScore).Take(maxLinks - selected.Count);

            foreach (LinkCandidate? c in remaining)
            {
                selected.Add(c);
                _ = usedUrls.Add(c.Page.Url);
            }
        }

        return selected.Take(maxLinks).ToList();
    }

    private async Task ComputeSemanticScoresWithEmbeddingsAsync(IEmbeddingCapability embeddingProvider, string articleTopic, string articleSummary, List<LinkCandidate> candidates, CancellationToken cancellationToken)
    {
        string queryText = string.IsNullOrWhiteSpace(articleSummary) ? articleTopic : $"{articleTopic}. {articleSummary}";

        float[] queryEmbedding = await embeddingProvider.CreateEmbeddingAsync(queryText, cancellationToken);

        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            ComputeSemanticScoresWithKeywords(articleTopic, articleSummary, candidates);
            return;
        }

        int batchCount = (candidates.Count + EmbeddingBatchSize - 1) / EmbeddingBatchSize;

        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = candidates.Skip(batchIndex * EmbeddingBatchSize).Take(EmbeddingBatchSize).ToList();

            var texts = batch.Select(c => BuildPageText(c.Page)).ToList();

            try
            {
                List<float[]> embeddings = await embeddingProvider.CreateBatchEmbeddingsAsync(texts, cancellationToken);

                for (int i = 0; i < batch.Count && i < embeddings.Count; i++)
                {
                    if (embeddings[i] != null && embeddings[i].Length > 0)
                    {
                        double similarity = CosineSimilarity(queryEmbedding, embeddings[i]);
                        batch[i].SemanticScore = similarity * 100;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute embeddings for batch {BatchIndex}", batchIndex);
            }
        }
    }

    private static void ComputeSemanticScoresWithKeywords(string articleTopic, string articleSummary, List<LinkCandidate> candidates)
    {
        HashSet<string> keywords = ExtractKeywords($"{articleTopic} {articleSummary}");

        foreach (LinkCandidate candidate in candidates)
        {
            string pageText = BuildPageText(candidate.Page).ToLowerInvariant();
            int matchCount = keywords.Count(k => pageText.Contains(k));
            candidate.SemanticScore = Math.Min(100, matchCount * 15);
        }
    }

    private async Task GenerateAnchorTextsAsync(ILlmProvider llm, ResourceBinding llmBinding, string articleTopic, string articleSummary, List<LinkCandidate> candidates, NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken)
    {
        int batchCount = (candidates.Count + LlmBatchSize - 1) / LlmBatchSize;
        services.PublishStatus($"Generating anchor texts for {candidates.Count} pages...");

        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var batch = candidates.Skip(batchIndex * LlmBatchSize).Take(LlmBatchSize).ToList();

            var prompt = new StringBuilder();
            _ = prompt.AppendLine($"Generate natural anchor text for internal links in an article about:");
            _ = prompt.AppendLine($"TOPIC: {articleTopic}");
            if (!string.IsNullOrWhiteSpace(articleSummary))
            {
                _ = prompt.AppendLine($"SUMMARY: {articleSummary}");
            }
            _ = prompt.AppendLine();
            _ = prompt.AppendLine("TARGET PAGES:");

            foreach (LinkCandidate? c in batch)
            {
                _ = prompt.AppendLine($"[{c.Id}] {c.Page.Title ?? ExtractTitleFromUrl(c.Page.Url)}");
                if (!string.IsNullOrEmpty(c.Page.MetaDescription))
                {
                    string desc = c.Page.MetaDescription.Length > 60 ? c.Page.MetaDescription.Substring(0, 60) + "..." : c.Page.MetaDescription;
                    _ = prompt.AppendLine($"    {desc}");
                }
            }

            _ = prompt.AppendLine();
            _ = prompt.AppendLine("For each page, provide:");
            _ = prompt.AppendLine("- anchor_text: short, natural anchor text (2-5 words)");
            _ = prompt.AppendLine("- placement_hint: where in the article to place this link");

            try
            {
                OperationResult<LlmResponse> result = await llm.CompleteAsync(
                    llmBinding,
                    prompt.ToString(),
                    new LlmRequestOptions(SystemPrompt: "Generate natural anchor texts for internal links. Keep them short and contextual.", Temperature: 0.3, MaxTokens: 800, ResponseSchema: AnchorSchemaJson),
                    cancellationToken
                );

                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Value?.Content))
                {
                    AnchorResponse? response = JsonSerializer.Deserialize<AnchorResponse>(result.Value.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (response?.Anchors != null)
                    {
                        foreach (AnchorItem anchor in response.Anchors)
                        {
                            LinkCandidate? candidate = batch.FirstOrDefault(c => c.Id == anchor.Id);
                            if (candidate != null)
                            {
                                candidate.SuggestedAnchor = anchor.AnchorText;
                                candidate.PlacementHint = anchor.PlacementHint;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate anchors for batch {BatchIndex}", batchIndex);
            }
        }
    }

    private static ContentPillar? FindPillarByUrl(SiteStructure structure, string url)
    {
        // Normalize URL for comparison
        string normalizedUrl = NormalizeUrl(url);

        foreach (ContentPillar pillar in structure.Pillars)
        {
            ContentPillar? found = FindPillarRecursive(pillar, normalizedUrl);
            if (found != null)
                return found;
        }
        return null;
    }

    private static ContentPillar? FindPillarRecursive(ContentPillar pillar, string normalizedUrl)
    {
        if (NormalizeUrl(pillar.PillarPage.Url).Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
            return pillar;

        foreach (ContentPillar subPillar in pillar.SubPillars ?? [])
        {
            ContentPillar? found = FindPillarRecursive(subPillar, normalizedUrl);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Normalize URL for comparison: remove trailing slash, lowercase
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        return string.IsNullOrEmpty(url) ? "" : url.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Get all pillar URLs in the structure (for diagnostics)
    /// </summary>
    private static List<string> GetAllPillarUrls(SiteStructure structure)
    {
        var urls = new List<string>();
        foreach (ContentPillar pillar in structure.Pillars)
        {
            CollectPillarUrls(pillar, urls);
        }
        return urls;
    }

    private static void CollectPillarUrls(ContentPillar pillar, List<string> urls)
    {
        urls.Add(pillar.PillarPage.Url);
        foreach (ContentPillar subPillar in pillar.SubPillars ?? [])
        {
            CollectPillarUrls(subPillar, urls);
        }
    }

    private static ContentPillar? FindParentPillar(SiteStructure structure, ContentPillar target)
    {
        foreach (ContentPillar pillar in structure.Pillars)
        {
            ContentPillar? parent = FindParentRecursive(pillar, target.PillarPage.Url);
            if (parent != null)
                return parent;
        }
        return null;
    }

    private static ContentPillar? FindParentRecursive(ContentPillar current, string targetUrl)
    {
        foreach (ContentPillar subPillar in current.SubPillars ?? [])
        {
            if (subPillar.PillarPage.Url.Equals(targetUrl, StringComparison.OrdinalIgnoreCase))
                return current;

            ContentPillar? found = FindParentRecursive(subPillar, targetUrl);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Infer the best parent pillar based on topic similarity
    /// </summary>
    private async Task<ContentPillar?> InferBestPillarAsync(SiteStructure structure, string articleTopic, string articleSummary, NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken)
    {
        if (structure.Pillars.Count == 0)
            return null;

        // Collect all pillars (including sub-pillars) with their text
        var pillarCandidates = new List<(ContentPillar Pillar, string Text, double Score)>();

        foreach (ContentPillar pillar in structure.Pillars)
        {
            CollectPillarsRecursive(pillar, pillarCandidates);
        }

        if (pillarCandidates.Count == 0)
            return null;

        // Score each pillar by keyword matching (fast, no API call)
        HashSet<string> topicKeywords = ExtractKeywords($"{articleTopic} {articleSummary}");

        foreach ((ContentPillar? pillar, string? text, double _) in pillarCandidates.ToList())
        {
            string pillarText = text.ToLowerInvariant();
            int matchCount = topicKeywords.Count(k => pillarText.Contains(k));
            int keywordsFromPillar = pillar.Keywords?.Count(k => topicKeywords.Any(tk => k.ToLowerInvariant().Contains(tk) || tk.Contains(k.ToLowerInvariant()))) ?? 0;

            int score = (matchCount * 10) + (keywordsFromPillar * 15);

            // Update score in list
            int index = pillarCandidates.FindIndex(p => p.Pillar == pillar);
            if (index >= 0)
            {
                pillarCandidates[index] = (pillar, text, score);
            }
        }

        // Return the best match
        (ContentPillar Pillar, string Text, double Score) best = pillarCandidates.OrderByDescending(p => p.Score).FirstOrDefault();

        if (best.Score > 0)
        {
            services.PublishStatus($"   Pillar scores: {string.Join(", ", pillarCandidates.OrderByDescending(p => p.Score).Take(3).Select(p => $"{p.Pillar.PillarPage.Title}={p.Score:F0}"))}");
            return best.Pillar;
        }

        // Fallback: return the first pillar with content
        return structure.Pillars.FirstOrDefault(p => IsContentPage(p.PillarPage.Url));
    }

    private static void CollectPillarsRecursive(ContentPillar pillar, List<(ContentPillar Pillar, string Text, double Score)> result)
    {
        if (IsContentPage(pillar.PillarPage.Url))
        {
            string text = BuildPillarText(pillar);
            result.Add((pillar, text, 0));
        }

        foreach (ContentPillar subPillar in pillar.SubPillars ?? [])
        {
            CollectPillarsRecursive(subPillar, result);
        }
    }

    private static string BuildPillarText(ContentPillar pillar)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(pillar.PillarPage.Title))
            parts.Add(pillar.PillarPage.Title);
        if (!string.IsNullOrEmpty(pillar.SeoTitle))
            parts.Add(pillar.SeoTitle);
        if (!string.IsNullOrEmpty(pillar.PillarPage.MetaDescription))
            parts.Add(pillar.PillarPage.MetaDescription);
        if (pillar.Keywords?.Count > 0)
            parts.Add(string.Join(" ", pillar.Keywords));

        return string.Join(". ", parts);
    }

    private static bool IsContentPage(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
        string lowerUrl = url.ToLowerInvariant();

        // Check URL patterns
        return !JunkUrlPatterns.Any(pattern => lowerUrl.Contains(pattern));
    }

    private static bool IsContentPage(SitemapPage page)
    {
        if (page == null || string.IsNullOrEmpty(page.Url))
            return false;

        // ✅ Exclude noindex pages
        if (!page.IsIndexable)
            return false;

        // Check URL
        if (!IsContentPage(page.Url))
            return false;

        // Check title patterns
        string title = page.Title ?? "";
        if (JunkTitlePatterns.Any(pattern => title.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            // Exception: if the title has real content before the pattern
            string beforePattern = title.Split(" - ").FirstOrDefault() ?? "";
            if (beforePattern.Length < 10)
                return false;
        }

        // Check for empty/thin content indicators
        return !string.IsNullOrWhiteSpace(page.MetaDescription) || !string.IsNullOrWhiteSpace(page.H1) || page.InboundLinkCount != 0;
    }

    private static double ComputeStructuralScore(SitemapPage page, string category)
    {
        double score = category switch
        {
            "parent-pillar" => 50,
            "sibling-pillar" => 40,
            "same-pillar-leaf" => 35,
            "other-pillar" => 30,
            "cross-pillar-leaf" => 20,
            _ => 10,
        };

        score += Math.Min(10, page.PageRankScore * 5);
        return score;
    }

    private static string BuildPageText(SitemapPage page)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(page.Title))
            parts.Add(page.Title);
        if (!string.IsNullOrEmpty(page.H1) && page.H1 != page.Title)
            parts.Add(page.H1);
        if (!string.IsNullOrEmpty(page.MetaDescription))
            parts.Add(page.MetaDescription);
        return string.Join(". ", parts);
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "the", "and", "for", "with", "from", "this", "that", "have", "has", "are", "was", "were", "will", "can", "your", "our", "their", "about" };

        return text.ToLowerInvariant().Split([' ', ',', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4 && !stopWords.Contains(w)).ToHashSet();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;
        double dotProduct = 0,
            normA = 0,
            normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator > 0 ? dotProduct / denominator : 0;
    }

    private static string ExtractTitleFromUrl(string url)
    {
        try
        {
            string path = new Uri(url).AbsolutePath.Trim('/');
            string lastSegment = path.Split('/').LastOrDefault() ?? "page";
            return lastSegment.Replace('-', ' ').Replace('_', ' ');
        }
        catch
        {
            return "Link";
        }
    }

    private static string ScoreToRelevance(double score) =>
        score switch
        {
            >= 70 => "high",
            >= 40 => "medium",
            _ => "low",
        };

    private sealed class CategorizedPages
    {
        public SitemapPage? ParentPillarPage { get; set; }
        public List<SitemapPage> SiblingPillars { get; set; } = [];
        public List<SitemapPage> SamePillarLeaves { get; set; } = [];
        public List<SitemapPage> OtherPillars { get; set; } = [];
        public List<SitemapPage> CrossPillarLeaves { get; set; } = [];
    }

    private sealed class LinkQuotas
    {
        public int ParentPillar { get; set; }
        public int SiblingPillars { get; set; }
        public int SamePillarLeaves { get; set; }
        public int OtherPillars { get; set; }
        public int CrossPillarLeaves { get; set; }
    }

    private sealed class LinkCandidate
    {
        public int Id { get; set; }
        public SitemapPage Page { get; set; } = new();
        public string Category { get; set; } = "";
        public double StructuralScore { get; set; }
        public double SemanticScore { get; set; }
        public double FinalScore { get; set; }
        public string? SuggestedAnchor { get; set; }
        public string? PlacementHint { get; set; }
    }

    private sealed class AnchorResponse
    {
        public List<AnchorItem> Anchors { get; set; } = [];
    }

    private sealed class AnchorItem
    {
        public int Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("anchor_text")]
        public string AnchorText { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("placement_hint")]
        public string? PlacementHint { get; set; }
    }

    /// <summary>
    /// Parses site structure from JsonElement input. Internal conversion from generic JSON to typed structure.
    /// </summary>
    private static SiteStructure ParseStructureFromJson(JsonElement element)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<SiteStructure>(element.GetRawText(), options) ?? throw new InvalidOperationException("Failed to parse site structure from JSON.");
    }
}
