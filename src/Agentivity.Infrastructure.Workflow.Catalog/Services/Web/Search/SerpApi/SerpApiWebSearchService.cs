using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.SerpApi;

public sealed class SerpApiWebSearchService : IWebSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILogger<SerpApiWebSearchService> _logger;

    public SerpApiWebSearchService(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILogger<SerpApiWebSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ProviderId => "serpapi";

    public string ProviderDisplayName => "SerpApi";

    public async ValueTask<WebSearchResponse> SearchAsync(string credentialsId, WebSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentNullException.ThrowIfNull(query);

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("SerpApi credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://serpapi.com/search.json")!;
        string effectiveQuery = query.BuildEffectiveQuery();
        string requestUrl = BuildRequestUrl(baseUrl, effectiveQuery, apiKey, query.Language, Math.Clamp(query.TopK, 1, 10));
        string debugRequestUrl = BuildDebugRequestUrl(baseUrl, effectiveQuery, query.Language, Math.Clamp(query.TopK, 1, 10));

        _logger.LogInformation("Sending SerpApi web search for query '{Query}' (effective query: '{EffectiveQuery}').", query.Query, effectiveQuery);
        _logger.LogInformation("SerpApi request URL: {RequestUrl}", debugRequestUrl);

        WebRateLimitSendResult sendResult = await WebRateLimiting.SendAsync(_httpClientFactory.CreateClient(), () => new HttpRequestMessage(HttpMethod.Get, requestUrl), query.RateLimitProfile, _logger, kind: "web_search", provider: ProviderId, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = sendResult.Response;
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new WebRequestDebugException($"SerpApi search failed with status {(int)response.StatusCode}: {payload}", WebSearchDebugTrace.Enrich(sendResult.DebugTrace, query.Query, effectiveQuery, debugRequestUrl, payload));
        }

        using var document = JsonDocument.Parse(payload);
        var results = new List<WebSearchResultItem>();

        if (document.RootElement.TryGetProperty("organic_results", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            int rank = 1;
            foreach (JsonElement item in items.EnumerateArray())
            {
                string? title = item.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() : null;
                string? url = item.TryGetProperty("link", out JsonElement linkElement) ? linkElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                string snippet = item.TryGetProperty("snippet", out JsonElement snippetElement) ? snippetElement.GetString() ?? string.Empty : string.Empty;

                results.Add(new WebSearchResultItem(rank++, title.Trim(), url.Trim(), snippet.Trim()));
            }
        }

        _logger.LogInformation("SerpApi returned {Count} result(s) for query '{Query}'", results.Count, effectiveQuery);

        return new WebSearchResponse(ProviderId, ProviderDisplayName, query.Query, effectiveQuery, results, WebSearchDebugTrace.Enrich(sendResult.DebugTrace, query.Query, effectiveQuery, debugRequestUrl));
    }

    private static string BuildRequestUrl(string baseUrl, string effectiveQuery, string apiKey, string? language, int topK)
    {
        var parameters = new List<string> { "engine=google", $"q={Uri.EscapeDataString(effectiveQuery)}", $"num={topK}", $"api_key={Uri.EscapeDataString(apiKey)}" };

        if (!string.IsNullOrWhiteSpace(language))
        {
            parameters.Add($"hl={Uri.EscapeDataString(language.Trim())}");
        }

        string separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", parameters)}";
    }

    private static string BuildDebugRequestUrl(string baseUrl, string effectiveQuery, string? language, int topK)
    {
        var parameters = new List<string> { "engine=google", $"q={Uri.EscapeDataString(effectiveQuery)}", $"num={topK}", "api_key=***" };

        if (!string.IsNullOrWhiteSpace(language))
        {
            parameters.Add($"hl={Uri.EscapeDataString(language.Trim())}");
        }

        string separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", parameters)}";
    }
}
