using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Http;

/// <summary>How an answer is read for an agent: <c>auto</c> (from its type), <c>html_text</c>, <c>json</c> or <c>raw_text</c>.</summary>
public enum WebAnswerMode
{
    Auto,
    HtmlText,
    Json,
    RawText,
}

/// <summary>An answer as an agent reads it: a page as plain text (with its title, and its links when asked), JSON as data, the rest as text.</summary>
public sealed record WebAnswerContent(string Mode, string? Title, string? Text, JsonNode? Json, JsonArray? Links, bool Truncated);

/// <summary>Reads an answer for an agent: what the HTTP Request tool gives it back.</summary>
public static class WebAnswerReading
{
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    public static WebAnswerMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "html_text" => WebAnswerMode.HtmlText,
            "json" => WebAnswerMode.Json,
            "raw_text" => WebAnswerMode.RawText,
            _ => WebAnswerMode.Auto,
        };

    public static WebAnswerContent Read(Uri uri, string? contentType, string body, WebAnswerMode mode, bool extractLinks, int maxChars)
    {
        WebAnswerMode actual = mode != WebAnswerMode.Auto ? mode
            : contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? WebAnswerMode.Json
            : contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true ? WebAnswerMode.HtmlText
            : WebAnswerMode.RawText;

        return actual switch
        {
            WebAnswerMode.Json => ReadJson(body, maxChars),
            WebAnswerMode.HtmlText => ReadHtml(uri, body, extractLinks, maxChars),
            _ => Text("text", body, maxChars),
        };
    }

    private static WebAnswerContent ReadJson(string body, int maxChars)
    {
        if (body.Length <= maxChars)
        {
            try
            {
                return new WebAnswerContent("json", null, null, JsonNode.Parse(body), null, false);
            }
            catch (JsonException)
            {
                // Not JSON after all: given as text.
            }
        }

        return Text("json", body, maxChars);
    }

    private static WebAnswerContent ReadHtml(Uri uri, string body, bool extractLinks, int maxChars)
    {
        var document = new HtmlDocument();
        document.LoadHtml(body);

        string? title = document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        if (document.DocumentNode.SelectNodes("//script|//style") is { } removable)
        {
            foreach (HtmlNode node in removable)
                node.Remove();
        }

        var (text, truncated) = Truncate(Normalize(document.DocumentNode.InnerText), maxChars);

        JsonArray? links = null;
        if (extractLinks)
        {
            links = [];
            foreach (HtmlNode link in document.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string? href = link.GetAttributeValue("href", string.Empty)?.Trim();
                if (string.IsNullOrWhiteSpace(href))
                    continue;

                links.Add(new JsonObject { ["href"] = Uri.TryCreate(uri, href, out Uri? resolved) ? resolved.ToString() : href, ["text"] = Normalize(link.InnerText) });
            }
        }

        return new WebAnswerContent("html", string.IsNullOrWhiteSpace(title) ? null : title, text, null, links, truncated);
    }

    private static WebAnswerContent Text(string mode, string body, int maxChars)
    {
        var (text, truncated) = Truncate(body, maxChars);
        return new WebAnswerContent(mode, null, text, null, null, truncated);
    }

    private static string Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : WhitespaceRegex.Replace(HtmlEntity.DeEntitize(text), " ").Trim();

    private static (string Text, bool Truncated) Truncate(string value, int maxChars) => value.Length <= maxChars ? (value, false) : (value[..maxChars], true);
}
