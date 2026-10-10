using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Agentivity.Infrastructure.Web.Cleaning;

/// <summary>
/// Turns a web page into what a reader needs: its readable content as Markdown or text, and the data it embeds for machines.
/// It works by taking noise out, never by choosing what to keep — it knows no site and no field name — and it removes only
/// by what an element <em>is</em> (script, style, navigation, a hidden element, a dialog…), never by matching words in class
/// names or ids: a wrapper called "overflow-hidden" is layout, not a hidden element, and the price may well sit inside it.
/// </summary>
public static partial class HtmlContentCleaner
{
    private static readonly HashSet<string> CodeAndStyles = new(StringComparer.Ordinal) { "script", "style", "noscript", "template", "link", "meta", "head", "base" };
    private static readonly HashSet<string> GraphicsAndEmbeds = new(StringComparer.Ordinal) { "svg", "iframe", "canvas", "object", "embed", "audio", "video", "source", "track", "map", "area" };
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal) { "button", "input", "select", "textarea", "option", "optgroup", "datalist" };
    private static readonly HashSet<string> FurnitureRoles = new(StringComparer.OrdinalIgnoreCase) { "navigation", "contentinfo", "complementary", "banner" };
    private static readonly HashSet<string> DialogRoles = new(StringComparer.OrdinalIgnoreCase) { "dialog", "alertdialog" };

    // A footer inside one of these is part of that item (a card's footer), not the page's footer.
    private static readonly HashSet<string> ContentContainers = new(StringComparer.Ordinal) { "article", "section", "main", "li" };

    /// <summary>Cleans <paramref name="html"/>. <paramref name="baseUri"/> turns relative links and image addresses into absolute ones.</summary>
    public static CleanedPage Clean(string html, Uri? baseUri = null, CleaningOptions? options = null)
    {
        options ??= new CleaningOptions();
        var stats = new CleaningStats { InputChars = html.Length };
        if (html.Length > options.MaxParsedChars)
        {
            html = html[..options.MaxParsedChars];
        }

        var document = new HtmlDocument { OptionMaxNestedChildNodes = 2000 };
        try
        {
            document.LoadHtml(html);
        }
        catch (Exception)
        {
            // A page nested beyond reason: read it as plain text rather than not at all.
            string plain = Regex.Replace(HtmlEntity.DeEntitize(TagPattern().Replace(html, " ")), @"\s+", " ").Trim();
            string cut = plain.Length > options.MaxContentChars ? plain[..options.MaxContentChars] : plain;
            stats.MarkdownChars = cut.Length;
            stats.MarkdownTruncated = cut.Length < plain.Length;
            return new CleanedPage(cut, cut, null, stats);
        }

        // The embedded data lives in elements the noise removal deletes: read it first (compacting comes later, once the page's content is known).
        var collected = options.MaxStructuredChars > 0 ? StructuredDataCollector.Collect(document) : null;

        RemoveNoise(document.DocumentNode, stats);

        string markdown = new ContentRenderer(true, baseUri).Render(document.DocumentNode, options.MaxContentChars, out bool truncated);
        string text = new ContentRenderer(false, baseUri).Render(document.DocumentNode, options.MaxContentChars, out _);

        // The readable content tells which parts of the data matter: what the page says is what the data is about.
        string? structured = null;
        if (collected is { Count: > 0 })
        {
            using JsonDocument parsed = JsonDocument.Parse(collected.ToJsonString());
            structured = JsonCompactor.Compact(parsed.RootElement, options.MaxStructuredChars, stats, text)?.ToJsonString();
        }

        stats.MarkdownChars = markdown.Length;
        stats.MarkdownTruncated = truncated;
        stats.StructuredChars = structured?.Length ?? 0;
        return new CleanedPage(markdown, text, structured, stats);
    }

    private static void RemoveNoise(HtmlNode root, CleaningStats stats)
    {
        var pending = new Stack<HtmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            HtmlNode node = pending.Pop();
            foreach (HtmlNode child in node.ChildNodes.ToList())
            {
                if (child.NodeType == HtmlNodeType.Comment)
                {
                    child.Remove();
                }
                else if (child.NodeType == HtmlNodeType.Element)
                {
                    string? family = NoiseFamily(child);
                    if (family is null)
                    {
                        pending.Push(child);
                    }
                    else
                    {
                        child.Remove();
                        stats.Count(family);
                    }
                }
            }
        }
    }

    /// <summary>Why an element is noise, or <c>null</c> when it is not. Decided by what the element is — never by words in its class or id.</summary>
    private static string? NoiseFamily(HtmlNode element)
    {
        string name = element.Name.ToLowerInvariant();
        if (CodeAndStyles.Contains(name))
        {
            return "code_and_styles";
        }

        if (GraphicsAndEmbeds.Contains(name))
        {
            return "graphics_and_embeds";
        }

        if (Controls.Contains(name))
        {
            return "controls";
        }

        // Hidden on purpose: the HTML attribute, or an inline style that says so. (Not a class name, not aria-hidden: a screen-reader
        // duplicate is removed as a repeat later, but the visible price must never be taken for hidden.)
        if (element.Attributes.Contains("hidden") || IsHiddenByStyle(element.GetAttributeValue("style", string.Empty)))
        {
            return "hidden";
        }

        string role = element.GetAttributeValue("role", string.Empty).Trim();
        if (name == "dialog" || DialogRoles.Contains(role) || string.Equals(element.GetAttributeValue("aria-modal", string.Empty), "true", StringComparison.OrdinalIgnoreCase))
        {
            return "dialogs";
        }

        if (name is "nav" or "aside" || FurnitureRoles.Contains(role))
        {
            return "page_furniture";
        }

        if (name == "footer" && !HasAncestor(element, ContentContainers))
        {
            return "page_furniture";
        }

        return null;
    }

    private static bool HasAncestor(HtmlNode node, HashSet<string> names)
    {
        for (HtmlNode? current = node.ParentNode; current is not null; current = current.ParentNode)
        {
            if (current.NodeType == HtmlNodeType.Element && names.Contains(current.Name.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHiddenByStyle(string style) => style.Length > 0 && HiddenStyle().IsMatch(style);

    [GeneratedRegex(@"(?:^|;)\s*(?:display\s*:\s*none|visibility\s*:\s*hidden)\s*(?:!\s*important\s*)?(?:;|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HiddenStyle();

    [GeneratedRegex(@"<script[\s\S]*?</script>|<style[\s\S]*?</style>|<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
