using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.ContentExtraction;

/// <summary>
/// Extracts main content from HTML pages, excluding navigation, sidebars, footers, and boilerplate.
/// Uses a multi-signal approach: structural analysis + text density + DOM depth.
/// </summary>
public sealed class MainContentExtractor
{
    private readonly ContentExtractionOptions _options;

    // Selectors for elements to EXCLUDE (noise)
    private static readonly string[] ExcludeSelectors =
    [
        "nav",
        "header",
        "footer",
        "aside",
        ".sidebar",
        ".navigation",
        ".menu",
        ".nav",
        ".footer",
        ".header",
        ".related-posts",
        ".related",
        ".comments",
        ".comment",
        ".social-share",
        ".share",
        ".breadcrumb",
        ".breadcrumbs",
        ".pagination",
        ".widget",
        ".widgets",
        ".ad",
        ".ads",
        ".advertisement",
        "#sidebar",
        "#navigation",
        "#menu",
        "#footer",
        "#header",
        "[role='navigation']",
        "[role='banner']",
        "[role='contentinfo']",
        ".wp-block-categories",
        ".wp-block-archives",
        ".wp-block-tag-cloud",
        ".elementor-widget-sidebar",
        ".elementor-widget-nav-menu",
    ];

    // Selectors for likely main content containers
    private static readonly string[] MainContentSelectors = ["article", "main", "[role='main']", ".post-content", ".entry-content", ".article-content", ".content", ".main-content", ".page-content", "#content", "#main", "#main-content", ".elementor-widget-theme-post-content"];

    public MainContentExtractor(ContentExtractionOptions? options = null)
    {
        _options = options ?? new ContentExtractionOptions();
    }

    /// <summary>
    /// Extract clean main content from HTML.
    /// Returns structured content with positional information.
    /// </summary>
    public ExtractedContent Extract(string html, string url)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new ExtractedContent
        {
            Url = url, // Step 1: Extract metadata BEFORE removing nodes
            Title = ExtractTitle(doc),
            H1 = ExtractH1(doc),
            MetaDescription = ExtractMetaDescription(doc),
        };

        // Step 2: Remove noise elements
        RemoveNoiseElements(doc);

        // Step 3: Find main content container
        HtmlNode? mainContainer = FindMainContentContainer(doc);

        // Fallback: use body with highest text density region
        mainContainer ??= FindHighestDensityRegion(doc);

        if (mainContainer == null)
        {
            result.MainText = "";
            result.Chunks = [];
            return result;
        }

        // Step 4: Extract structured content
        result.Chunks = ExtractChunks(mainContainer);
        result.MainText = string.Join("\n\n", result.Chunks.Select(c => c.Text));

        // Step 5: Extract headings hierarchy
        result.Headings = ExtractHeadingsHierarchy(mainContainer);

        return result;
    }

    private void RemoveNoiseElements(HtmlDocument doc)
    {
        var nodesToRemove = new List<HtmlNode>();

        foreach (string selector in ExcludeSelectors)
        {
            try
            {
                HtmlNodeCollection nodes = doc.DocumentNode.SelectNodes(ConvertCssSelectorToXPath(selector));
                if (nodes != null)
                {
                    nodesToRemove.AddRange(nodes);
                }
            }
            catch { }
        }

        // Remove script, style, noscript
        HtmlNodeCollection scriptsAndStyles = doc.DocumentNode.SelectNodes("//script|//style|//noscript|//iframe|//svg");
        if (scriptsAndStyles != null)
        {
            nodesToRemove.AddRange(scriptsAndStyles);
        }

        foreach (HtmlNode? node in nodesToRemove.Distinct())
        {
            try
            {
                node.Remove();
            }
            catch { }
        }
    }

    private HtmlNode? FindMainContentContainer(HtmlDocument doc)
    {
        foreach (string selector in MainContentSelectors)
        {
            try
            {
                string xpath = ConvertCssSelectorToXPath(selector);
                HtmlNode node = doc.DocumentNode.SelectSingleNode(xpath);
                if (node != null && GetTextDensity(node) > _options.MinTextDensity)
                {
                    return node;
                }
            }
            catch { }
        }
        return null;
    }

    private HtmlNode? FindHighestDensityRegion(HtmlDocument doc)
    {
        HtmlNode body = doc.DocumentNode.SelectSingleNode("//body");
        if (body == null)
            return null;

        // Find all block-level elements and score by text density
        HtmlNodeCollection candidates = body.SelectNodes(".//div|.//section|.//article") ?? new HtmlNodeCollection(body);

        HtmlNode? best = null;
        double bestScore = 0;

        foreach (HtmlNode node in candidates)
        {
            double density = GetTextDensity(node);
            int textLength = GetCleanText(node).Length;

            // Score = density * sqrt(length) - penalize very short blocks
            double score = density * Math.Sqrt(Math.Max(0, textLength - 100));

            if (score > bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }

    private double GetTextDensity(HtmlNode node)
    {
        string text = GetCleanText(node);
        string html = node.InnerHtml;

        if (string.IsNullOrEmpty(html))
            return 0;

        // Text density = text chars / html chars
        return (double)text.Length / html.Length;
    }

    private List<ContentChunk> ExtractChunks(HtmlNode container)
    {
        var chunks = new List<ContentChunk>();
        int position = 0;

        // Extract paragraphs, headings, and list items as separate chunks
        HtmlNodeCollection textNodes = container.SelectNodes(".//p|.//h1|.//h2|.//h3|.//h4|.//h5|.//h6|.//li|.//blockquote");

        if (textNodes == null)
        {
            // Fallback: split by double newlines
            string fullText = GetCleanText(container);
            var paragraphs = Regex.Split(fullText, @"\n\s*\n").Where(p => p.Trim().Length > _options.MinChunkLength).ToList();

            foreach (string? para in paragraphs)
            {
                chunks.Add(
                    new ContentChunk
                    {
                        Text = para.Trim(),
                        Type = ChunkType.Paragraph,
                        Position = position++,
                        Weight = 1.0,
                    }
                );
            }
            return chunks;
        }

        foreach (HtmlNode node in textNodes)
        {
            string text = GetCleanText(node).Trim();
            if (text.Length < _options.MinChunkLength)
                continue;

            ChunkType chunkType = node.Name.ToLowerInvariant() switch
            {
                "h1" => ChunkType.H1,
                "h2" => ChunkType.H2,
                "h3" => ChunkType.H3,
                "h4" or "h5" or "h6" => ChunkType.Heading,
                "li" => ChunkType.ListItem,
                "blockquote" => ChunkType.Quote,
                _ => ChunkType.Paragraph,
            };

            // Weight by position and type
            double weight = CalculateChunkWeight(chunkType, position, chunks.Count);

            chunks.Add(
                new ContentChunk
                {
                    Text = text,
                    Type = chunkType,
                    Position = position++,
                    Weight = weight,
                }
            );
        }

        return chunks;
    }

    private double CalculateChunkWeight(ChunkType type, int position, int totalChunks)
    {
        // Positional weight: early content is more important
        double positionWeight = 1.0 - (0.3 * position / Math.Max(1, totalChunks));

        // Type weight: headings > paragraphs > list items
        double typeWeight = type switch
        {
            ChunkType.H1 => 2.0,
            ChunkType.H2 => 1.5,
            ChunkType.H3 => 1.3,
            ChunkType.Heading => 1.2,
            ChunkType.Paragraph => 1.0,
            ChunkType.Quote => 0.9,
            ChunkType.ListItem => 0.8,
            _ => 1.0,
        };

        return positionWeight * typeWeight;
    }

    private List<HeadingInfo> ExtractHeadingsHierarchy(HtmlNode container)
    {
        var headings = new List<HeadingInfo>();
        HtmlNodeCollection nodes = container.SelectNodes(".//h1|.//h2|.//h3|.//h4|.//h5|.//h6");

        if (nodes == null)
            return headings;

        foreach (HtmlNode node in nodes)
        {
            int level = int.Parse(node.Name.Substring(1));
            string text = GetCleanText(node).Trim();

            if (!string.IsNullOrEmpty(text))
            {
                headings.Add(new HeadingInfo { Level = level, Text = text });
            }
        }

        return headings;
    }

    private string? ExtractTitle(HtmlDocument doc)
    {
        HtmlNode titleNode = doc.DocumentNode.SelectSingleNode("//title");
        return titleNode != null ? GetCleanText(titleNode).Trim() : null;
    }

    private string? ExtractH1(HtmlDocument doc)
    {
        HtmlNode h1Node = doc.DocumentNode.SelectSingleNode("//h1");
        return h1Node != null ? GetCleanText(h1Node).Trim() : null;
    }

    private string? ExtractMetaDescription(HtmlDocument doc)
    {
        HtmlNode metaNode = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
        return metaNode?.GetAttributeValue("content", null);
    }

    private static string GetCleanText(HtmlNode node)
    {
        string text = node.InnerText;
        // Normalize whitespace
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    private static string ConvertCssSelectorToXPath(string cssSelector)
    {
        // Simple conversion for common patterns
        return cssSelector.StartsWith(".") ? $"//*[contains(@class, '{cssSelector.Substring(1)}')]"
            : cssSelector.StartsWith("#") ? $"//*[@id='{cssSelector.Substring(1)}']"
            : cssSelector.StartsWith("[") ? $"//*{cssSelector}"
            : $"//{cssSelector}";
    }
}

public sealed class ContentExtractionOptions
{
    public double MinTextDensity { get; set; } = 0.1;
    public int MinChunkLength { get; set; } = 30;
}

public sealed class ExtractedContent
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? H1 { get; set; }
    public string? MetaDescription { get; set; }
    public string MainText { get; set; } = "";
    public List<ContentChunk> Chunks { get; set; } = [];
    public List<HeadingInfo> Headings { get; set; } = [];
}

public sealed class ContentChunk
{
    public string Text { get; set; } = "";
    public ChunkType Type { get; set; }
    public int Position { get; set; }
    public double Weight { get; set; } = 1.0;
    public float[]? Embedding { get; set; }
}

public enum ChunkType
{
    Paragraph,
    H1,
    H2,
    H3,
    Heading,
    ListItem,
    Quote,
}

public sealed class HeadingInfo
{
    public int Level { get; set; }
    public string Text { get; set; } = "";
}
