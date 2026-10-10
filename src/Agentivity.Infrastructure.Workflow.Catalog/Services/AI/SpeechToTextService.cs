using System.Net.Http.Headers;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

public sealed record TranscriptionResult(string Text, string? Language, double? DurationSeconds);

public sealed class SpeechToTextService
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public SpeechToTextService(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TranscriptionResult> TranscribeAsync(string credentialsId, string audioSource, string? language, string? contextPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioSource);
        (byte[] audioBytes, string fileName) = await ResolveAudioAsync(audioSource, cancellationToken).ConfigureAwait(false);
        return await TranscribeAsync(credentialsId, audioBytes, fileName, language, contextPrompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Transcribes raw audio bytes directly — no disk round-trip, unlike the
    /// <see cref="TranscribeAsync(string, string, string?, string?, CancellationToken)"/>
    /// overload's URL/local-file-path resolution, which a transient chat upload doesn't need.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(string credentialsId, byte[] audioBytes, string fileName, string? language, string? contextPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentNullException.ThrowIfNull(audioBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.openai.com/v1");

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(fileName));
        content.Add(audioContent, "file", fileName);
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Add(new StringContent(language), "language");
        }
        if (!string.IsNullOrWhiteSpace(contextPrompt))
        {
            content.Add(new StringContent(contextPrompt), "prompt");
        }

        HttpClient httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string url = $"{baseUrl.TrimEnd('/')}/audio/transcriptions";
        _logger.LogInformation("Transcribing audio via {Url} ({SizeBytes} bytes)", url, audioBytes.Length);

        HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Speech-to-text request failed. Status: {StatusCode}, Response: {Response}", (int)response.StatusCode, responseBody);
            throw new InvalidOperationException($"Speech-to-text API returned {response.StatusCode}: {responseBody.Substring(0, Math.Min(200, responseBody.Length))}");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;

        string text = root.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        string? detectedLanguage = root.TryGetProperty("language", out JsonElement languageElement) ? languageElement.GetString() : null;
        double? duration = root.TryGetProperty("duration", out JsonElement durationElement) && durationElement.TryGetDouble(out double d) ? d : null;

        return new TranscriptionResult(text, detectedLanguage, duration);
    }

    private static async Task<(byte[] Bytes, string FileName)> ResolveAudioAsync(string audioSource, CancellationToken cancellationToken)
    {
        // Windows' "Copy as path" wraps the clipboard value in double quotes — strip them so a
        // pasted path doesn't silently fail File.Exists.
        audioSource = audioSource.Trim().Trim('"');

        if (Uri.TryCreate(audioSource, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var httpClient = new HttpClient();
            byte[] bytes = await httpClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            return (bytes, Path.GetFileName(uri.LocalPath) is { Length: > 0 } name ? name : "audio.mp3");
        }

        if (!File.Exists(audioSource))
        {
            throw new InvalidOperationException($"Audio source not found: '{audioSource}'. Provide a local file path or an http(s) URL.");
        }

        byte[] fileBytes = await File.ReadAllBytesAsync(audioSource, cancellationToken).ConfigureAwait(false);
        return (fileBytes, Path.GetFileName(audioSource));
    }

    private static string GuessMimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
}
