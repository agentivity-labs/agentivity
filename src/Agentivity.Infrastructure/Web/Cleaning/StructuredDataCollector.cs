using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Agentivity.Infrastructure.Web.Cleaning;

/// <summary>
/// Gathers the data a page publishes for machines, as it is, without reading any of it: JSON-LD blocks, JSON the page embeds to
/// start its own scripts (a framework's initial state), the standard meta tags, and microdata. Run it before the noise is
/// removed — the blocks live in the very elements that get removed.
/// </summary>
internal static partial class StructuredDataCollector
{
    private const int MaxScriptChars = 6_000_000;
    private const int MaxMicrodataItems = 200;

    // Open Graph, Twitter cards and the other vocabularies pages use to describe themselves.
    private static readonly string[] MetaPrefixes = ["og:", "twitter:", "product:", "article:", "business:", "book:", "profile:", "music:", "video:"];
    private static readonly string[] MetaNames = ["description", "keywords", "author", "title"];

    public static JsonObject Collect(HtmlDocument document)
    {
        var result = new JsonObject();

        var jsonLd = new JsonArray();
        var state = new JsonArray();
        foreach (HtmlNode script in document.DocumentNode.Descendants("script").ToList())
        {
            string text = script.InnerText.Trim();
            if (text.Length == 0 || text.Length > MaxScriptChars)
            {
                continue;
            }

            string type = script.GetAttributeValue("type", string.Empty).Trim().ToLowerInvariant();
            if (type == "application/ld+json")
            {
                AddJson(jsonLd, text);
            }
            else if (type.Contains("json", StringComparison.Ordinal) || script.Id is "__NEXT_DATA__" or "__NUXT_DATA__")
            {
                AddJson(state, text);
            }
            else if (type is "" or "text/javascript" or "module")
            {
                // `window.__STATE__ = {...};` — the assigned value, when (and only when) it is valid JSON.
                try
                {
                    Match assignment = StateAssignment().Match(text);
                    if (assignment.Success)
                    {
                        AddJson(state, assignment.Groups["json"].Value);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A script too tangled to read as an assignment — skip it.
                }
            }
        }

        AddIfAny(result, "jsonld", jsonLd);
        AddIfAny(result, "state", state);
        AddIfAny(result, "meta", CollectMeta(document));
        AddIfAny(result, "microdata", CollectMicrodata(document));
        return result;
    }

    private static void AddIfAny(JsonObject target, string name, JsonNode? value)
    {
        if (value is JsonArray { Count: > 0 } || value is JsonObject { Count: > 0 })
        {
            target[name] = value;
        }
    }

    private static void AddJson(JsonArray target, string text)
    {
        // A page may wrap the payload in an HTML comment or a CDATA section.
        text = text.Trim();
        if (text.StartsWith("<!--", StringComparison.Ordinal) && text.EndsWith("-->", StringComparison.Ordinal))
        {
            text = text[4..^3].Trim();
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 64 });
            target.Add(JsonNode.Parse(parsed.RootElement.GetRawText()));
        }
        catch (JsonException)
        {
            // Not JSON after all (a script, not data) — nothing to keep.
        }
    }

    private static JsonObject CollectMeta(HtmlDocument document)
    {
        var meta = new JsonObject();
        foreach (HtmlNode node in document.DocumentNode.Descendants("meta"))
        {
            string key = node.GetAttributeValue("property", node.GetAttributeValue("name", string.Empty)).Trim().ToLowerInvariant();
            string content = HtmlEntity.DeEntitize(node.GetAttributeValue("content", string.Empty)).Trim();
            if (key.Length == 0 || content.Length == 0 || meta.ContainsKey(key))
            {
                continue;
            }

            if (MetaPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)) || MetaNames.Contains(key))
            {
                meta[key] = content;
            }
        }

        return meta;
    }

    private static JsonArray CollectMicrodata(HtmlDocument document)
    {
        var scopes = new JsonArray();
        foreach (HtmlNode scope in document.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element && n.Attributes.Contains("itemscope")).Take(MaxMicrodataItems))
        {
            var props = new JsonObject();
            foreach (HtmlNode prop in scope.Descendants().Where(n => n.NodeType == HtmlNodeType.Element && n.Attributes.Contains("itemprop")))
            {
                if (NearestScope(prop) != scope)
                {
                    continue; // belongs to a nested item
                }

                string name = prop.GetAttributeValue("itemprop", string.Empty).Trim();
                string value = PropertyValue(prop);
                if (name.Length > 0 && value.Length > 0 && !props.ContainsKey(name))
                {
                    props[name] = value;
                }
            }

            if (props.Count > 0)
            {
                var item = new JsonObject();
                string type = scope.GetAttributeValue("itemtype", string.Empty).Trim();
                if (type.Length > 0)
                {
                    item["type"] = type;
                }

                item["props"] = props;
                scopes.Add(item);
            }
        }

        return scopes;
    }

    private static HtmlNode? NearestScope(HtmlNode node)
    {
        for (HtmlNode? current = node.ParentNode; current is not null; current = current.ParentNode)
        {
            if (current.NodeType == HtmlNodeType.Element && current.Attributes.Contains("itemscope"))
            {
                return current;
            }
        }

        return null;
    }

    private static string PropertyValue(HtmlNode prop)
    {
        foreach (string attribute in new[] { "content", "datetime", "href", "src", "value" })
        {
            string value = prop.GetAttributeValue(attribute, string.Empty).Trim();
            if (value.Length > 0)
            {
                return HtmlEntity.DeEntitize(value);
            }
        }

        return Regex.Replace(HtmlEntity.DeEntitize(prop.InnerText), @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"^\s*(?:window\.|self\.|globalThis\.|var\s+|let\s+|const\s+)?[A-Za-z_$][\w$.]*\s*=\s*(?<json>[\[{][\s\S]*[\]}])\s*;?\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex StateAssignment();
}
