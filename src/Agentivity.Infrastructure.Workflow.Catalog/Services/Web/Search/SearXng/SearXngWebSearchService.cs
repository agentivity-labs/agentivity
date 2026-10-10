using System.Net.Http.Headers;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SearXng;

public sealed class SearXngWebSearchService : IWebSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILogger<SearXngWebSearchService> _logger;

    public SearXngWebSearchService(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILogger<SearXngWebSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ProviderId => "searxng";

    public string ProviderDisplayName => "SearXNG";

    public async ValueTask<WebSearchResponse> SearchAsync(string credentialsId, WebSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentNullException.ThrowIfNull(query);

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string baseUrl = credentials.GetField("base_url") ?? throw new InvalidOperationException("SearXNG credentials missing 'base_url' field.");
        string effectiveQuery = query.BuildEffectiveQuery();
        string requestUrl = BuildRequestUrl(baseUrl, effectiveQuery, query.Language);

        _logger.LogInformation("Sending SearXNG web search for query '{Query}' (effective query: '{EffectiveQuery}').", query.Query, effectiveQuery);
        _logger.LogInformation("SearXNG request URL: {RequestUrl}", requestUrl);

        WebRateLimitSendResult sendResult = await WebRateLimiting
            .SendAsync(
                _httpClientFactory.CreateClient(),
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    return request;
                },
                query.RateLimitProfile,
                _logger,
                kind: "web_search",
                provider: ProviderId,
                cancellationToken
            )
            .ConfigureAwait(false);
        using HttpResponseMessage response = sendResult.Response;
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new WebRequestDebugException($"SearXNG search failed with status {(int)response.StatusCode}: {payload}", WebSearchDebugTrace.Enrich(sendResult.DebugTrace, query.Query, effectiveQuery, requestUrl, payload));
        }

        using var document = JsonDocument.Parse(payload);
        var results = new List<WebSearchResultItem>();

        if (document.RootElement.TryGetProperty("results", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            int rank = 1;
            foreach (JsonElement item in items.EnumerateArray())
            {
                string? title = item.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() : null;
                string? url = item.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                string snippet = item.TryGetProperty("content", out JsonElement contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;

                results.Add(new WebSearchResultItem(rank++, title.Trim(), url.Trim(), snippet.Trim()));
                if (results.Count >= Math.Clamp(query.TopK, 1, 10))
                {
                    break;
                }
            }
        }

        _logger.LogInformation("SearXNG returned {Count} result(s) for query '{Query}'", results.Count, effectiveQuery);

        return new WebSearchResponse(ProviderId, ProviderDisplayName, query.Query, effectiveQuery, results, WebSearchDebugTrace.Enrich(sendResult.DebugTrace, query.Query, effectiveQuery, requestUrl));
    }

    private static string BuildRequestUrl(string baseUrl, string effectiveQuery, string? language)
    {
        string normalizedBaseUrl = baseUrl.TrimEnd('/');
        string searchUrl = normalizedBaseUrl.EndsWith("/search", StringComparison.OrdinalIgnoreCase) ? normalizedBaseUrl : $"{normalizedBaseUrl}/search";

        var parameters = new List<string> { $"q={Uri.EscapeDataString(effectiveQuery)}", "format=json", "categories=general", "safesearch=0" };

        if (!string.IsNullOrWhiteSpace(language))
        {
            parameters.Add($"language={Uri.EscapeDataString(language.Trim())}");
        }

        return $"{searchUrl}?{string.Join("&", parameters)}";
    }
}
