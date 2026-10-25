using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Web;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Models;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Web.Search.Tavily;

public sealed class TavilyWebSearchService : IWebSearchService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialsService _credentialsService;
    private readonly ILogger<TavilyWebSearchService> _logger;

    public TavilyWebSearchService(IHttpClientFactory httpClientFactory, CredentialsService credentialsService, ILogger<TavilyWebSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ProviderId => "tavily";

    public string ProviderDisplayName => "Tavily";

    public async ValueTask<WebSearchResponse> SearchAsync(string credentialsId, WebSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentNullException.ThrowIfNull(query);

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Tavily credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.tavily.com/search")!;
        string effectiveQuery = query.BuildEffectiveQuery();

        _logger.LogInformation("Sending Tavily web search for query '{Query}' (effective query: '{EffectiveQuery}').", query.Query, effectiveQuery);
        _logger.LogInformation("Tavily request URL: {RequestUrl}", baseUrl);

        WebRateLimitSendResult sendResult = await WebRateLimiting
            .SendAsync(
                _httpClientFactory.CreateClient(),
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(
                            new
                            {
                                api_key = apiKey,
                                query = effectiveQuery,
                                search_depth = "basic",
                                topic = "general",
                                max_results = Math.Clamp(query.TopK, 1, 10),
                                include_answer = false,
                                include_images = false,
                                include_raw_content = false,
                            },
                            SerializerOptions
                        ),
                        Encoding.UTF8,
                        "application/json"
                    );
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
            throw new WebRequestDebugException($"Tavily search failed with status {(int)response.StatusCode}: {payload}", WebSearchDebugTrace.Enrich(sendResult.DebugTrace, query.Query, effectiveQuery, baseUrl, payload));
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
            }
        }

        _logger.LogInformation("Tavily returned {Count} result(s) for query '{Query}'", results.Count, effectiveQuery);

        return new WebSearchResponse(ProviderId, ProviderDisplayName, query.Query, effectiveQuery, results, WebSearchDebugTrace.Enrich(sendResult.DebugTrace, query.Query, effectiveQuery, baseUrl));
    }
}
