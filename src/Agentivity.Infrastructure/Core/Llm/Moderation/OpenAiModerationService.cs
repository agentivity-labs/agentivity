using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm.Moderation;

/// <summary>
/// Calls OpenAI's <c>/v1/moderations</c> endpoint — free, same "openai" credential template
/// chat/Whisper/embeddings already reuse.
/// </summary>
public sealed class OpenAiModerationService : IModerationService
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiModerationService> _logger;

    public OpenAiModerationService(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<OpenAiModerationService> logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ModerationResult> CheckAsync(string credentialsId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("OpenAI credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.openai.com/v1");

        using var request = new StringContent(JsonSerializer.Serialize(new { input = content }), Encoding.UTF8, "application/json");

        HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string url = $"{baseUrl.TrimEnd('/')}/moderations";
        HttpResponseMessage response = await httpClient.PostAsync(url, request, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI moderation request failed. Status: {StatusCode}, Response: {Response}", (int)response.StatusCode, responseBody);
            throw new InvalidOperationException($"OpenAI moderation API returned {response.StatusCode}: {responseBody.Substring(0, Math.Min(200, responseBody.Length))}");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return new ModerationResult(false, []);
        }

        JsonElement first = results[0];
        bool flagged = first.TryGetProperty("flagged", out JsonElement flaggedElement) && flaggedElement.ValueKind == JsonValueKind.True;

        var categories = new List<string>();
        if (first.TryGetProperty("categories", out JsonElement categoriesElement) && categoriesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty category in categoriesElement.EnumerateObject())
            {
                if (category.Value.ValueKind == JsonValueKind.True)
                {
                    categories.Add(category.Name);
                }
            }
        }

        return new ModerationResult(flagged, categories);
    }
}
