using System.Net;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// A request as a generation of an API writes it: the provider (named in messages), where it goes under the provider's
/// address, its headers, its body, and the model (which some transports address outside the body, as Bedrock does).
/// </summary>
internal sealed record LlmWireRequest(string Provider, string Path, Dictionary<string, object?> Body, IReadOnlyDictionary<string, string> Headers, string Model);

/// <summary>
/// What the provider answered: its body, or why there is none (a refusal described the platform's way, a network failure, a
/// timeout). <paramref name="WasBadRequest"/>: the provider answered 400 — the only refusal a request may be adapted to.
/// </summary>
internal sealed record LlmWireReply(OperationResult<string> Body, bool WasBadRequest);

/// <summary>How a request reaches the provider — over HTTP at its address, or through AWS for Bedrock. It knows nothing of how the request is written.</summary>
internal interface ILlmTransport
{
    Task<LlmWireReply> SendAsync(LlmWireRequest request, CancellationToken cancellationToken);
}

/// <summary>The request posted as JSON to the provider's address, followed by the path the generation names.</summary>
internal sealed class HttpLlmTransport : ILlmTransport
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public HttpLlmTransport(IHttpClientFactory httpClientFactory, string baseUrl, ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LlmWireReply> SendAsync(LlmWireRequest request, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = RequestTimeout;
        var endpoint = $"{_baseUrl}{request.Path}";
        // A key may travel in the query (Gemini's does): what is logged stops before it.
        var shownEndpoint = endpoint.Split('?')[0];

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        foreach (var (name, value) in request.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        var requestJson = JsonSerializer.Serialize(request.Body, SerializerOptions);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        _logger.LogDebug("[LLM][{Provider}] --> {Endpoint} chars={Chars}", request.Provider, shownEndpoint, requestJson.Length);
        _logger.LogDebug("[LLM][{Provider}] --> payload{NewLine}{Payload}", request.Provider, Environment.NewLine, requestJson);

        try
        {
            using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogError("{Provider} API error: {StatusCode} {Body}", request.Provider, httpResponse.StatusCode, responseBody);
                return new LlmWireReply(OperationResult<string>.Failure(LlmProviderError.Describe(request.Provider, httpResponse.StatusCode, ErrorMessageOf(responseBody))), httpResponse.StatusCode == HttpStatusCode.BadRequest);
            }

            TryExtractUsage(responseBody, out var promptTokens, out var completionTokens, out var totalTokens);
            _logger.LogDebug("[LLM][{Provider}] <-- {StatusCode} prompt_tokens={PromptTokens} completion_tokens={CompletionTokens} total_tokens={TotalTokens}", request.Provider, (int)httpResponse.StatusCode, promptTokens, completionTokens, totalTokens);
            _logger.LogDebug("[LLM][{Provider}] <-- response{NewLine}{Body}", request.Provider, Environment.NewLine, responseBody);

            return new LlmWireReply(OperationResult<string>.Success(responseBody), WasBadRequest: false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "{Provider} API request failed", request.Provider);
            return new LlmWireReply(OperationResult<string>.Failure($"{request.Provider} request failed: {ex.Message}"), WasBadRequest: false);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Provider} API request was canceled by the caller", request.Provider);
            return new LlmWireReply(OperationResult<string>.Failure($"{request.Provider} request was canceled before completion."), WasBadRequest: false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "{Provider} API request timed out after {Timeout}", request.Provider, RequestTimeout);
            return new LlmWireReply(OperationResult<string>.Failure($"{request.Provider} request timed out after {RequestTimeout.TotalSeconds}s. The model may need more time for complex prompts."), WasBadRequest: false);
        }
    }

    /// <summary>The provider's own words in an error body (<c>{ "error": { "message": … } }</c>, which they all use), or the start of the body.</summary>
    internal static string ErrorMessageOf(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? responseBody;
            }
        }
        catch (JsonException) { }

        return responseBody.Length > 200 ? responseBody[..200] + "..." : responseBody;
    }

    private static void TryExtractUsage(string responseJson, out int promptTokens, out int completionTokens, out int totalTokens)
    {
        promptTokens = 0;
        completionTokens = 0;
        totalTokens = 0;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            // OpenAI: { usage: { prompt_tokens, completion_tokens, total_tokens } }
            // Anthropic: { usage: { input_tokens, output_tokens } }
            if (!root.TryGetProperty("usage", out var usage))
                return;
            if (usage.TryGetProperty("prompt_tokens", out var pt))
                promptTokens = pt.GetInt32();
            else if (usage.TryGetProperty("input_tokens", out var it))
                promptTokens = it.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct))
                completionTokens = ct.GetInt32();
            else if (usage.TryGetProperty("output_tokens", out var ot))
                completionTokens = ot.GetInt32();
            if (usage.TryGetProperty("total_tokens", out var tt))
                totalTokens = tt.GetInt32();
            else
                totalTokens = promptTokens + completionTokens;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
    }
}
