using System.Globalization;
using System.Xml.Linq;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>The latest entries of a news feed or a blog (RSS or Atom), each entry a document. Public addresses only.</summary>
public sealed class RssKnowledgeSource(IHttpClientFactory httpClientFactory) : KnowledgeSourceType
{
    public const string Id = "rss";

    public override string TypeId => Id;

    public override int Icon => 0xe544; // Material: rss_feed

    public override string Name => "RSS/Atom feed";

    public override string Description => "The latest entries of a news feed or a blog (RSS or Atom).";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
    [
        Input("feed_url", ArgumentType.Text, "Address of the RSS or Atom feed.", ui: new ArgumentUiMetadata { Group = "Feed", Order = 10 }, required: true),
        Input("headers_json", ArgumentType.Text, "Extra HTTP headers, as a JSON object.", "{}", ui: new ArgumentUiMetadata { Group = "Feed", Order = 20, Multiline = true, Rows = 6, Format = "json" }, required: false),
        Input("max_items", ArgumentType.Number, "Most entries read from the feed.", 20, ui: new ArgumentUiMetadata { Group = "Feed", Order = 30, Min = 1, Step = 1 }, required: false),
        Input("timeout_seconds", ArgumentType.Number, "Maximum time allowed, in seconds.", 30, ui: new ArgumentUiMetadata { Group = "Network", Order = 40, Min = 1, Step = 1 }, required: false),
    ];

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        var feedUrl = settings.RequireString("feed_url");
        var maxItems = settings.GetInt("max_items") ?? 20;
        var xml = await KnowledgeSourceSupport.GetTextAsync(httpClientFactory, feedUrl, KnowledgeSourceSupport.ParseHeaders(settings.GetString("headers_json")), TimeSpan.FromSeconds(settings.GetInt("timeout_seconds") ?? 30), cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        return string.Equals(document.Root?.Name.LocalName, "feed", StringComparison.OrdinalIgnoreCase) ? Atom(document, feedUrl, maxItems) : Rss(document, feedUrl, maxItems);
    }

    private static List<KnowledgeDocument> Rss(XDocument document, string feedUrl, int maxItems) =>
        [
            .. document
                .Descendants("item")
                .Take(maxItems)
                .Select((item, index) =>
                    Entry(
                        feedUrl,
                        "rss",
                        index + 1,
                        item.Element("title")?.Value?.Trim(),
                        item.Element("link")?.Value?.Trim(),
                        item.Element("pubDate")?.Value?.Trim(),
                        FirstNonEmpty(item.Element("encoded")?.Value, item.Element("description")?.Value, item.Value)?.Trim()
                    )
                )
                .OfType<KnowledgeDocument>(),
        ];

    private static List<KnowledgeDocument> Atom(XDocument document, string feedUrl, int maxItems)
    {
        var atom = document.Root?.Name.Namespace ?? XNamespace.None;
        return
        [
            .. document
                .Descendants(atom + "entry")
                .Take(maxItems)
                .Select((entry, index) =>
                    Entry(
                        feedUrl,
                        "atom",
                        index + 1,
                        entry.Element(atom + "title")?.Value?.Trim(),
                        entry.Elements(atom + "link").FirstOrDefault(link => string.Equals((string?)link.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))?.Attribute("href")?.Value ?? entry.Element(atom + "link")?.Attribute("href")?.Value,
                        entry.Element(atom + "updated")?.Value?.Trim() ?? entry.Element(atom + "published")?.Value?.Trim(),
                        FirstNonEmpty(entry.Element(atom + "content")?.Value, entry.Element(atom + "summary")?.Value, entry.Value)?.Trim()
                    )
                )
                .OfType<KnowledgeDocument>(),
        ];
    }

    private static KnowledgeDocument? Entry(string feedUrl, string format, int index, string? title, string? link, string? publishedAt, string? content)
    {
        var text = string.IsNullOrWhiteSpace(title) ? content : string.IsNullOrWhiteSpace(content) ? title : $"{title}\n\n{content}";
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new KnowledgeDocument
            {
                Text = text.Trim(),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = feedUrl,
                    ["feed_url"] = feedUrl,
                    ["response_mode"] = format,
                    ["item_index"] = index.ToString(CultureInfo.InvariantCulture),
                    ["title"] = title ?? string.Empty,
                    ["item_url"] = link ?? string.Empty,
                    ["published_at"] = publishedAt ?? string.Empty,
                },
            };
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
