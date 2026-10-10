using System.Text;
using HtmlAgilityPack;

namespace Agentivity.Infrastructure.Web.Cleaning;

/// <summary>
/// Writes a cleaned page as Markdown (headings, lists, tables, links, image addresses) or as plain text. Only text, link
/// targets and image addresses survive — every other attribute is dropped by construction, not by a removal rule.
/// </summary>
internal sealed class ContentRenderer
{
    private const int MaxDepth = 200;
    private const int DuplicateMinChars = 40;

    /// <summary>A link longer than this is a redirect with an opaque tracking token (an ad click): unreadable, costly, and the reader cannot use it.</summary>
    private const int MaxLinkChars = 500;

    /// <summary>Pictures may carry long resizing parameters, so they get more room than links.</summary>
    private const int MaxImageAddressChars = 1000;

    /// <summary>This many consecutive blocks that are only a small number is a ladder (a quantity picker, pagination), not content.</summary>
    private const int NumberLadderMinRun = 8;

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "main", "header", "footer", "ul", "ol", "li", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr",
        "blockquote", "pre", "figure", "figcaption", "address", "details", "summary", "form", "fieldset", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "body",
    };

    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "gclid", "dclid", "fbclid", "msclkid", "yclid", "igshid", "mc_cid", "mc_eid", "_ga", "_gl", "cmpid",
    };

    private readonly bool _markdown;
    private readonly Uri? _baseUri;
    private readonly List<(string Text, bool Tight)> _blocks = [];
    private readonly StringBuilder _line = new();
    private readonly Stack<(bool Ordered, int Index)> _lists = new();
    private bool _pendingSpace;
    private string? _marker;
    private int _quote;

    public ContentRenderer(bool markdown, Uri? baseUri)
    {
        _markdown = markdown;
        _baseUri = baseUri;
    }

    /// <summary>Renders <paramref name="root"/>, cut at a block boundary when longer than <paramref name="maxChars"/>.</summary>
    public string Render(HtmlNode root, int maxChars, out bool truncated)
    {
        Walk(root, 0);
        Flush();

        var output = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool previousTight = false;
        truncated = false;
        foreach ((string text, bool tight) in WithoutNumberLadders(_blocks))
        {
            // The same long sentence printed again (a label repeated on every tile, an accessible copy of a visible text) adds nothing.
            // Short values are never collapsed: two neighbouring products can cost the same.
            if (text.Length >= DuplicateMinChars && !seen.Add(text))
            {
                continue;
            }

            string separator = output.Length == 0 ? string.Empty : tight && previousTight ? "\n" : "\n\n";
            if (output.Length + separator.Length + text.Length > maxChars)
            {
                truncated = true;
                break;
            }

            output.Append(separator).Append(text);
            previousTight = tight;
        }

        return output.ToString();
    }

    private static List<(string Text, bool Tight)> WithoutNumberLadders(List<(string Text, bool Tight)> blocks)
    {
        var result = new List<(string Text, bool Tight)>(blocks.Count);
        int start = 0;
        while (start < blocks.Count)
        {
            int end = start;
            while (end < blocks.Count && IsSmallNumber(blocks[end].Text))
            {
                end++;
            }

            if (end - start >= NumberLadderMinRun)
            {
                start = end; // a ladder: skip it whole
                continue;
            }

            if (end > start)
            {
                result.AddRange(blocks.Skip(start).Take(end - start)); // a short run of numbers is content
                start = end;
                continue;
            }

            result.Add(blocks[start]);
            start++;
        }

        return result;
    }

    private static bool IsSmallNumber(string text) => text.Length <= 3 && text.All(char.IsDigit);

    private void Walk(HtmlNode node, int depth)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            AppendText(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text));
            return;
        }

        if (node.NodeType is not (HtmlNodeType.Element or HtmlNodeType.Document))
        {
            return;
        }

        if (depth > MaxDepth)
        {
            AppendText(HtmlEntity.DeEntitize(node.InnerText)); // nested beyond reason: keep the words, not the structure
            return;
        }

        string name = node.NodeType == HtmlNodeType.Document ? "#document" : node.Name.ToLowerInvariant();
        switch (name)
        {
            case "br":
                Flush();
                return;
            case "hr":
                Flush();
                if (_markdown)
                {
                    AddBlock("---", tight: false);
                }

                return;
            case "img":
                RenderImage(node);
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                Flush();
                if (_markdown)
                {
                    Raw(new string('#', name[1] - '0') + " ");
                }

                WalkChildren(node, depth);
                Flush();
                return;
            case "a":
                RenderLink(node, depth);
                return;
            case "strong" or "b":
                RenderEmphasis(node, depth, "**");
                return;
            case "em" or "i":
                RenderEmphasis(node, depth, "*");
                return;
            case "code":
                RenderEmphasis(node, depth, "`");
                return;
            case "pre":
                RenderPreformatted(node);
                return;
            case "ul" or "ol":
                Flush();
                _lists.Push((name == "ol", 0));
                WalkChildren(node, depth);
                Flush();
                _lists.Pop();
                return;
            case "li":
                RenderListItem(node, depth);
                return;
            case "table":
                RenderTable(node);
                return;
            case "blockquote":
                Flush();
                _quote++;
                WalkChildren(node, depth);
                Flush();
                _quote--;
                return;
        }

        if (BlockElements.Contains(name))
        {
            Flush();
            WalkChildren(node, depth);
            Flush();
        }
        else
        {
            WalkChildren(node, depth);
        }
    }

    private void WalkChildren(HtmlNode node, int depth)
    {
        foreach (HtmlNode child in node.ChildNodes)
        {
            Walk(child, depth + 1);
        }
    }

    private void RenderListItem(HtmlNode node, int depth)
    {
        Flush();
        if (_lists.Count == 0)
        {
            WalkChildren(node, depth);
            Flush();
            return;
        }

        (bool ordered, int index) = _lists.Pop();
        index++;
        _lists.Push((ordered, index));
        string indent = new(' ', (_lists.Count - 1) * 2);
        _marker = _markdown ? indent + (ordered ? $"{index}. " : "- ") : indent + (ordered ? $"{index}. " : "- ");
        WalkChildren(node, depth);
        Flush();
        _marker = null;
    }

    private void RenderEmphasis(HtmlNode node, int depth, string mark)
    {
        if (!_markdown)
        {
            WalkChildren(node, depth);
            return;
        }

        int before = _line.Length;
        bool pending = _pendingSpace;
        Raw(mark);
        int inner = _line.Length;
        WalkChildren(node, depth);
        if (_line.Length == inner)
        {
            _line.Length = before; // nothing inside: no empty marks
            _pendingSpace = pending;
        }
        else
        {
            _line.Append(mark);
        }
    }

    private void RenderPreformatted(HtmlNode node)
    {
        Flush();
        string code = HtmlEntity.DeEntitize(node.InnerText).Trim('\r', '\n');
        if (code.Trim().Length == 0)
        {
            return;
        }

        if (code.Length > 2000)
        {
            code = code[..2000] + "…";
        }

        AddBlock(_markdown ? "```\n" + code + "\n```" : code, tight: false);
    }

    private void RenderImage(HtmlNode node)
    {
        if (!_markdown)
        {
            return;
        }

        // Pixel-sized images are trackers and spacers.
        if (SmallDimension(node, "width") || SmallDimension(node, "height"))
        {
            return;
        }

        string? source = ImageSource(node);
        if (source is null)
        {
            return;
        }

        string alt = Collapse(HtmlEntity.DeEntitize(node.GetAttributeValue("alt", node.GetAttributeValue("title", string.Empty)))).Replace("[", "(").Replace("]", ")");
        Raw($"![{alt}]({source})");
    }

    private string? ImageSource(HtmlNode node)
    {
        // A lazy-loaded image carries a placeholder in src and the real address in a data-* attribute or a srcset.
        foreach (string attribute in new[] { "src", "data-src", "data-lazy-src", "data-original", "data-lazy" })
        {
            string value = node.GetAttributeValue(attribute, string.Empty).Trim();
            if (value.Length > 0 && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return Absolute(value, keepQuery: true, MaxImageAddressChars);
            }
        }

        string srcset = node.GetAttributeValue("srcset", node.GetAttributeValue("data-srcset", string.Empty)).Trim();
        if (srcset.Length > 0)
        {
            string first = srcset.Split(',')[0].Trim().Split(' ')[0];
            return Absolute(first, keepQuery: true, MaxImageAddressChars);
        }

        return null;
    }

    private static bool SmallDimension(HtmlNode node, string attribute) => int.TryParse(node.GetAttributeValue(attribute, string.Empty), out int size) && size is >= 0 and <= 2;

    private void RenderLink(HtmlNode node, int depth)
    {
        string? target = Absolute(node.GetAttributeValue("href", string.Empty), keepQuery: true);
        if (!_markdown || target is null)
        {
            WalkChildren(node, depth);
            return;
        }

        bool containsBlocks = node.Descendants().Any(d => d.NodeType == HtmlNodeType.Element && BlockElements.Contains(d.Name.ToLowerInvariant()));
        if (containsBlocks)
        {
            // A whole tile wrapped in a link: keep its content as blocks and put the address after it, where a reader finds it.
            Flush();
            int blocksBefore = _blocks.Count;
            WalkChildren(node, depth);
            Flush();
            if (_blocks.Count > blocksBefore)
            {
                AddBlock($"<{target}>", tight: true); // a link around nothing readable says nothing
            }

            return;
        }

        int before = _line.Length;
        bool pending = _pendingSpace;
        Raw("[");
        int inner = _line.Length;
        WalkChildren(node, depth);
        if (_line.Length == inner)
        {
            _line.Length = before; // an empty link says nothing
            _pendingSpace = pending;
        }
        else
        {
            _line.Append("](").Append(target).Append(')');
        }
    }

    private void RenderTable(HtmlNode table)
    {
        Flush();
        bool first = true;
        foreach (HtmlNode row in table.Descendants("tr"))
        {
            var cells = row.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element && c.Name.ToLowerInvariant() is "td" or "th").ToList();
            if (cells.Count == 0)
            {
                continue;
            }

            List<string> texts = cells.Select(CellText).ToList();
            if (texts.All(t => t.Length == 0))
            {
                continue;
            }

            if (_markdown)
            {
                AddBlock("| " + string.Join(" | ", texts) + " |", tight: true);
                if (first)
                {
                    AddBlock("|" + string.Concat(Enumerable.Repeat(" --- |", texts.Count)), tight: true);
                }
            }
            else
            {
                AddBlock(string.Join(" | ", texts), tight: true);
            }

            first = false;
        }
    }

    private string CellText(HtmlNode cell)
    {
        var inner = new ContentRenderer(_markdown, _baseUri);
        inner.Walk(cell, 0);
        inner.Flush();
        return string.Join(" ", inner._blocks.Select(b => b.Text)).Replace("|", "/").Replace("\n", " ").Trim();
    }

    private string? Absolute(string address, bool keepQuery, int maxChars = MaxLinkChars)
    {
        address = HtmlEntity.DeEntitize(address).Trim(); // an attribute value still has its entities ("&amp;")
        if (address.Length == 0 || address.StartsWith('#') || address.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.RelativeOrAbsolute, out Uri? uri))
        {
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            if (_baseUri is null || !Uri.TryCreate(_baseUri, address, out uri))
            {
                return null;
            }
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        string query = keepQuery ? WithoutTracking(uri.Query) : string.Empty;
        string result = uri.GetLeftPart(UriPartial.Path) + query;
        return result.Length > maxChars ? null : result;
    }

    private static string WithoutTracking(string query)
    {
        if (query.Length <= 1)
        {
            return string.Empty;
        }

        IEnumerable<string> kept = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(pair =>
        {
            string key = pair.Split('=')[0];
            return !key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParameters.Contains(key);
        });
        string joined = string.Join("&", kept);
        return joined.Length == 0 ? string.Empty : "?" + joined;
    }

    private void AppendText(string text)
    {
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == ' ')
            {
                _pendingSpace = true;
                continue;
            }

            if (_pendingSpace && _line.Length > 0)
            {
                _line.Append(' ');
            }

            _pendingSpace = false;
            _line.Append(c);
        }
    }

    private void Raw(string text)
    {
        if (_pendingSpace && _line.Length > 0)
        {
            _line.Append(' ');
        }

        _pendingSpace = false;
        _line.Append(text);
    }

    private void Flush()
    {
        string text = _line.ToString().Trim();
        _line.Clear();
        _pendingSpace = false;
        if (text.Length == 0)
        {
            return;
        }

        string prefix = _markdown ? string.Concat(Enumerable.Repeat("> ", _quote)) : string.Empty;
        bool inList = _lists.Count > 0;
        string lead = _marker ?? (inList ? new string(' ', _lists.Count * 2) : string.Empty);
        _marker = null;
        AddBlock(prefix + lead + text, tight: inList);
    }

    private void AddBlock(string text, bool tight) => _blocks.Add((text, tight));

    private static string Collapse(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
}
