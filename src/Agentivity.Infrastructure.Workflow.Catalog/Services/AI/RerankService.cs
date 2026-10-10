using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

public sealed record RerankedDocument(int OriginalIndex, string Text, double Score);

/// <summary>
/// Calls Cohere's Rerank API — given a query and a list of documents, returns them reordered by
/// true relevance (a cross-encoder pass, more accurate but slower than the cosine-similarity
/// ranking a vector store's initial retrieval already did). Meant to run AFTER a vector-store
/// query narrows a large corpus down to a manageable candidate list, not as a replacement for it.
/// </summary>
public sealed class RerankService
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public RerankService(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<RerankedDocument>> RerankAsync(string credentialsId, string model, string query, IReadOnlyList<string> documents, int? topN, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (documents.Count == 0)
        {
            return [];
        }

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Cohere credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.cohere.com/v2");

        var body = new Dictionary<string, object?> { ["model"] = model, ["query"] = query, ["documents"] = documents };
        if (topN is { } n && n > 0)
        {
            body["top_n"] = n;
        }

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string url = $"{baseUrl.TrimEnd('/')}/rerank";
        HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Cohere rerank request failed. Status: {StatusCode}, Response: {Response}", (int)response.StatusCode, responseBody);
            throw new InvalidOperationException($"Cohere rerank API returned {response.StatusCode}: {responseBody.Substring(0, Math.Min(200, responseBody.Length))}");
        }

        return ParseResponse(responseBody, documents);
    }

    private static IReadOnlyList<RerankedDocument> ParseResponse(string responseBody, IReadOnlyList<string> documents)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        var results = new List<RerankedDocument>();

        if (!document.RootElement.TryGetProperty("results", out JsonElement resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (JsonElement item in resultsElement.EnumerateArray())
        {
            int index = item.GetProperty("index").GetInt32();
            if (index < 0 || index >= documents.Count)
            {
                continue;
            }

            double score = item.TryGetProperty("relevance_score", out JsonElement scoreElement) ? scoreElement.GetDouble() : 0;
            results.Add(new RerankedDocument(index, documents[index], score));
        }

        return results;
    }
}
