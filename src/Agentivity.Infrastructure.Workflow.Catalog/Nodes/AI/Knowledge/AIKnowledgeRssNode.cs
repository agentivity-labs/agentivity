using System.Text.Json;
using System.Xml.Linq;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeRssNode : ResourceNode, IKnowledgeSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AIKnowledgeRssNode> _logger;

    public AIKnowledgeRssNode(IHttpClientFactory httpClientFactory, ILogger<AIKnowledgeRssNode> logger)
        : base("ai.knowledge.rss")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (RSS/Atom)", "Loads a knowledge corpus from an RSS or Atom feed and maps entries to normalized documents.", "ai.knowledge");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput("feed_url", ArgumentType.Text, "RSS or Atom feed URL.", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput(
            "headers_json",
            ArgumentType.Text,
            "Optional extra HTTP headers as a JSON object.",
            "{}",
            ui: new ArgumentUiMetadata
            {
                Group = "Connection",
                Order = 20,
                Multiline = true,
                Rows = 6,
                Format = "json",
            }
        );
        DefineInput(
            "max_items",
            ArgumentType.Number,
            "Maximum number of feed items to load.",
            20,
            ui: new ArgumentUiMetadata
            {
                Group = "Feed",
                Order = 30,
                Min = 1,
                Step = 1,
            }
        );
        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "HTTP timeout in seconds.",
            30,
            ui: new ArgumentUiMetadata
            {
                Group = "Network",
                Order = 40,
                Min = 1,
                Step = 1,
            }
        );

        DefineIcon(NodeIcon.Embedded("rss"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string feedUrl = binding.RequireString("feed_url");
        int maxItems = binding.GetInt("max_items") ?? 20;
        int timeoutSeconds = binding.GetInt("timeout_seconds") ?? 30;
        IReadOnlyDictionary<string, string> headers = ParseAdditionalHeaders(binding.GetString("headers_json"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        foreach (KeyValuePair<string, string> header in headers)
        {
            _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpClient client = _httpClientFactory.CreateClient(nameof(AIKnowledgeRssNode));
        using HttpResponseMessage response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();

        string xml = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        _logger.LogInformation("Loaded knowledge feed from {FeedUrl}", feedUrl);

        return IsAtomFeed(document) ? ParseAtom(document, feedUrl, maxItems) : ParseRss(document, feedUrl, maxItems);
    }

    private static IReadOnlyList<KnowledgeDocument> ParseRss(XDocument document, string feedUrl, int maxItems)
    {
        var items = document.Descendants("item").Take(maxItems).Select((item, index) => ToRssDocument(item, feedUrl, index + 1)).Where(doc => doc is not null).Cast<KnowledgeDocument>().ToList();

        return items;
    }

    private static KnowledgeDocument? ToRssDocument(XElement item, string feedUrl, int index)
    {
        string? title = item.Element("title")?.Value?.Trim();
        string? link = item.Element("link")?.Value?.Trim();
        string? publishedAt = item.Element("pubDate")?.Value?.Trim();
        string? content = FirstNonEmpty(item.Element("encoded")?.Value, item.Element("description")?.Value, item.Value)?.Trim();

        string? text = ComposeDocumentText(title, content);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new KnowledgeDocument
            {
                Text = text,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = feedUrl,
                    ["feed_url"] = feedUrl,
                    ["response_mode"] = "rss",
                    ["item_index"] = index.ToString(),
                    ["title"] = title ?? string.Empty,
                    ["item_url"] = link ?? string.Empty,
                    ["published_at"] = publishedAt ?? string.Empty,
                },
            };
    }

    private static IReadOnlyList<KnowledgeDocument> ParseAtom(XDocument document, string feedUrl, int maxItems)
    {
        XNamespace atom = document.Root?.Name.Namespace ?? XNamespace.None;
        var entries = document.Descendants(atom + "entry").Take(maxItems).Select((entry, index) => ToAtomDocument(entry, atom, feedUrl, index + 1)).Where(doc => doc is not null).Cast<KnowledgeDocument>().ToList();

        return entries;
    }

    private static KnowledgeDocument? ToAtomDocument(XElement entry, XNamespace atom, string feedUrl, int index)
    {
        string? title = entry.Element(atom + "title")?.Value?.Trim();
        string? link = entry.Elements(atom + "link").FirstOrDefault(candidate => string.Equals((string?)candidate.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))?.Attribute("href")?.Value ?? entry.Element(atom + "link")?.Attribute("href")?.Value;
        string? publishedAt = entry.Element(atom + "updated")?.Value?.Trim() ?? entry.Element(atom + "published")?.Value?.Trim();
        string? content = FirstNonEmpty(entry.Element(atom + "content")?.Value, entry.Element(atom + "summary")?.Value, entry.Value)?.Trim();

        string? text = ComposeDocumentText(title, content);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new KnowledgeDocument
            {
                Text = text,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = feedUrl,
                    ["feed_url"] = feedUrl,
                    ["response_mode"] = "atom",
                    ["item_index"] = index.ToString(),
                    ["title"] = title ?? string.Empty,
                    ["item_url"] = link ?? string.Empty,
                    ["published_at"] = publishedAt ?? string.Empty,
                },
            };
    }

    private static bool IsAtomFeed(XDocument document) => string.Equals(document.Root?.Name.LocalName, "feed", StringComparison.OrdinalIgnoreCase);

    private static string? ComposeDocumentText(string? title, string? content)
    {
        return string.IsNullOrWhiteSpace(title)
            ? string.IsNullOrWhiteSpace(content)
                ? null
                : content.Trim()
            : string.IsNullOrWhiteSpace(content)
                ? title.Trim()
                : $"{title.Trim()}\n\n{content.Trim()}";
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyDictionary<string, string> ParseAdditionalHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(headersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The 'headers_json' RSS knowledge parameter must be a JSON object.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            }

            return headers;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The 'headers_json' RSS knowledge parameter is not valid JSON: {ex.Message}", ex);
        }
    }
}
