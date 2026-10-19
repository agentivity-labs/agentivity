using System.Text.Json;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Direct;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Firecrawl;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Jina;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Scrappers.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>What the data sources that read the web share: extra headers, reading an address, reading a page through a scraper.</summary>
public static class KnowledgeSourceSupport
{
    /// <summary>The scrapers a page can be read with.</summary>
    public static readonly (string Value, string Label)[] Scrapers = [("direct", "Direct (free)"), ("jina", "Jina Reader"), ("firecrawl", "Firecrawl (needs a key)")];

    /// <summary>Extra HTTP headers, given as a JSON object.</summary>
    public static IReadOnlyDictionary<string, string> ParseHeaders(string? headersJson)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(headersJson))
            return headers;
        try
        {
            using var document = JsonDocument.Parse(headersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The extra headers must be a JSON object.");
            foreach (var property in document.RootElement.EnumerateObject())
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            return headers;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The extra headers are not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Reads an address through the protected client: public addresses only, each redirect checked.</summary>
    public static async Task<string> GetTextAsync(IHttpClientFactory httpClientFactory, string url, IReadOnlyDictionary<string, string> headers, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"'{url}' is not a web address (http or https).");
        if (PublicNetwork.Precheck(uri) is { } refused)
            throw new InvalidOperationException(refused);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
            _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        try
        {
            using var response = await httpClientFactory.CreateClient(PublicNetwork.FollowingRedirectsHttpClientName).SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{url} answered {(int)response.StatusCode}.");
            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (PublicNetwork.RefusalOf(ex) is { } refusal)
        {
            throw new InvalidOperationException(refusal, ex);
        }
    }

    /// <summary>The settings of the sources that read pages: which scraper, its key, what is kept of a page.</summary>
    public static IEnumerable<ArgumentDefinition> PageSettings(int order) =>
    [
        new("scraper", ArgumentType.Choice, "How the pages are read.", "direct", null, ArgumentEditorMode.Fixed, [.. Scrapers.Select(s => new ChoiceOption(s.Value, s.Label))], null, new ArgumentUiMetadata { Group = "Scraping", Order = order }) { Required = false },
        new("scraper_credentials", ArgumentType.Credentials, "The Firecrawl key, when the pages are read with Firecrawl.", null, null, ArgumentEditorMode.Fixed, null, "firecrawl", new ArgumentUiMetadata { Group = "Scraping", Order = order + 1 }) { Required = false },
        new("max_content_length", ArgumentType.Number, "Maximum amount of content kept from each page.", 50000, ui: new ArgumentUiMetadata { Group = "Scraping", Order = order + 2, Min = 256, Step = 256 }) { Required = false },
        new("extract_metadata", ArgumentType.Boolean, "Extract page metadata when supported by the scraper.", true, ui: new ArgumentUiMetadata { Group = "Scraping", Order = order + 3 }) { Required = false },
        new("render_javascript", ArgumentType.Boolean, "Enable JavaScript rendering when supported by the scraper.", false, ui: new ArgumentUiMetadata { Group = "Scraping", Order = order + 4 }) { Required = false },
    ];

    public static ScrapeOptions ScrapeOptions(KnowledgeSourceSettings settings) =>
        new()
        {
            TimeoutSeconds = settings.GetInt("timeout_seconds") ?? 30,
            MaxContentLength = settings.GetInt("max_content_length") ?? 50000,
            ExtractMetadata = settings.GetBool("extract_metadata") ?? true,
            RenderJavaScript = settings.GetBool("render_javascript") ?? false,
            ExtractMarkdown = true,
        };

    public static IWebScraperService Scraper(string? provider, IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILoggerFactory loggerFactory) =>
        (provider ?? "direct").ToLowerInvariant() switch
        {
            "direct" => new DirectWebScraperService(httpClientFactory, loggerFactory),
            "jina" => new JinaWebScraperService(httpClientFactory, loggerFactory),
            "firecrawl" => new FirecrawlWebScraperService(httpClientFactory, credentialsService, loggerFactory),
            _ => throw new InvalidOperationException($"'{provider}' is not a scraper this installation has (direct, jina, firecrawl)."),
        };

    /// <summary>A page read through a scraper, as a document; null when it has no text.</summary>
    public static async Task<KnowledgeDocument?> ScrapeAsync(IWebScraperService scraper, string? credentialsId, string url, ScrapeOptions options, IReadOnlyDictionary<string, string>? extraMetadata, CancellationToken cancellationToken)
    {
        var result = await scraper.ScrapeAsync(new WebScrapeRequest(url, credentialsId, options), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"The page {url} could not be read: {result.Error ?? "unknown error"}");

        var content = new[] { result.Markdown, result.Text, result.Html }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var metadata = result.Metadata is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(result.Metadata, StringComparer.OrdinalIgnoreCase);
        metadata["source"] = url;
        metadata["url"] = url;
        metadata["key"] = url;
        metadata["scraper_provider"] = scraper.ProviderDisplayName;
        foreach (var item in extraMetadata ?? new Dictionary<string, string>())
            metadata[item.Key] = item.Value;
        return new KnowledgeDocument { Text = content, Metadata = metadata };
    }

    /// <summary>The lines of a text, one value each, without blanks or repeats.</summary>
    public static IReadOnlyList<string> Lines(string raw) => [.. raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}
