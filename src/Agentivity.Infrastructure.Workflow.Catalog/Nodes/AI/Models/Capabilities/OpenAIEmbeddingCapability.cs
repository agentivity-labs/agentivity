using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;

/// <summary>
/// OpenAI-based embedding capability.
/// Used by OpenAI Embeddings and by the vector stores that have OpenAI credentials of their own.
/// </summary>
public sealed class OpenAIEmbeddingCapability : IEmbeddingCapability
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger? _logger;

    public int EmbeddingDimensions =>
        ModelName switch
        {
            "text-embedding-3-small" => 1536,
            "text-embedding-3-large" => 3072,
            "text-embedding-ada-002" => 1536,
            _ => 1536,
        };

    public string ModelName { get; }

    public OpenAIEmbeddingCapability(IHttpClientFactory httpClientFactory, string apiKey, string baseUrl, string model, ILogger? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        ModelName = model ?? throw new ArgumentNullException(nameof(model));
        _logger = logger;
    }

    public async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

        var requestBody = new { input = text, model = ModelName };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

        string embeddingsUrl = $"{_baseUrl.TrimEnd('/')}/embeddings";

        try
        {
            HttpResponseMessage response = await httpClient.PostAsync(embeddingsUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogError("Embedding API request failed. Status: {StatusCode}, URL: {Url}, Model: {Model}, Response: {Response}", (int)response.StatusCode, embeddingsUrl, ModelName, errorBody);

                throw new InvalidOperationException($"Embedding API returned {response.StatusCode}. " + $"Provider may not support embeddings endpoint. " + $"Response: {errorBody.Substring(0, Math.Min(200, errorBody.Length))}");
            }

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            EmbeddingResponse? result = JsonSerializer.Deserialize<EmbeddingResponse>(responseJson);

            return result?.Data?[0]?.Embedding ?? throw new InvalidOperationException("Failed to get embedding from API");
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "HTTP request to embedding API failed. URL: {Url}, Provider may not support embeddings.", embeddingsUrl);
            throw new InvalidOperationException($"Failed to connect to embedding API at {embeddingsUrl}. " + $"If using LM Studio or local provider, ensure it supports the /embeddings endpoint.", ex);
        }
    }

    public async Task<List<float[]>> CreateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        var embeddings = new List<float[]>();

        foreach (string? text in textList)
        {
            float[] embedding = await CreateEmbeddingAsync(text, cancellationToken);
            embeddings.Add(embedding);

            // Small delay to avoid rate limiting
            await Task.Delay(50, cancellationToken);
        }

        return embeddings;
    }

    // As the API writes them: "data", "embedding".
    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; set; }
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
