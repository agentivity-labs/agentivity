using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

public sealed record SynthesizedAudio(byte[] Bytes, string MimeType);

public sealed class TextToSpeechService
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public TextToSpeechService(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(string credentialsId, string text, string model, string voice, string responseFormat, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.openai.com/v1");

        var requestBody = new { model, input = text, voice, response_format = responseFormat };

        HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string url = $"{baseUrl.TrimEnd('/')}/audio/speech";
        _logger.LogInformation("Synthesizing speech via {Url} ({Length} chars, voice={Voice})", url, text.Length, voice);

        using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("Text-to-speech request failed. Status: {StatusCode}, Response: {Response}", (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"Text-to-speech API returned {response.StatusCode}: {errorBody.Substring(0, Math.Min(200, errorBody.Length))}");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string mimeType = response.Content.Headers.ContentType?.MediaType ?? MimeTypeFromResponseFormat(responseFormat);

        return new SynthesizedAudio(bytes, mimeType);
    }

    private static string MimeTypeFromResponseFormat(string responseFormat) =>
        responseFormat.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "opus" => "audio/ogg",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "audio/mpeg",
        };
}
