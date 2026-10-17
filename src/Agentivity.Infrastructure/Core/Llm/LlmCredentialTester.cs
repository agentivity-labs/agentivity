using System.Net;
using System.Text.Json;
using CredentialModel = Agentivity.Domain.Credentials.Credentials;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>How a language-model credential answered the provider's "list the models" call.</summary>
public static class LlmCredentialTestStatus
{
    /// <summary>The key is accepted.</summary>
    public const string Valid = "valid";

    /// <summary>The provider rejected the key (or there is none).</summary>
    public const string InvalidKey = "invalid_key";

    /// <summary>The provider knows the key but refuses this call: a restricted key, which may still be able to chat.</summary>
    public const string Restricted = "restricted";

    public const string RateLimited = "rate_limited";

    /// <summary>The provider could not be reached.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>This kind of credential cannot be checked without a real request (for example AWS Bedrock).</summary>
    public const string NotTestable = "not_testable";

    public const string Error = "error";
}

/// <param name="Status">One of <see cref="LlmCredentialTestStatus"/>.</param>
/// <param name="Message">What to tell the person, in plain words.</param>
/// <param name="Models">The models the key can use (empty when the provider did not list any).</param>
/// <param name="ConfiguredModel">The <c>model</c> field of the credential, if any.</param>
/// <param name="ConfiguredModelAvailable">Whether <paramref name="ConfiguredModel"/> is among <paramref name="Models"/>; null when it cannot be told.</param>
public sealed record LlmCredentialTestResult(string Status, string Message, IReadOnlyList<string> Models, string? ConfiguredModel, bool? ConfiguredModelAvailable);

/// <summary>
/// Checks a language-model credential without spending anything: every provider has a "list the models" call that needs the key
/// but costs no token. A wrong key is told at once, and a right one comes back with the models it can use. The call is made from
/// the server with the stored credential, so the key never reaches the browser.
/// </summary>
public sealed class LlmCredentialTester(IHttpClientFactory httpClientFactory)
{
    private const string AnthropicVersion = "2023-06-01";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The name of the family of the connections to a model. It is not a kind of connection: asking for the connections of
    /// this family gives those of every kind in <see cref="LanguageModelTemplates"/>.
    /// </summary>
    public const string LanguageModelFamily = "llm";

    /// <summary>The credential templates that hold a language-model connection.</summary>
    public static readonly IReadOnlyList<string> LanguageModelTemplates = ["anthropic", "openai", "gemini", "xai", "deepseek", "aws_bedrock"];

    public static bool IsLanguageModelTemplate(string templateId) => LanguageModelTemplates.Contains(templateId, StringComparer.OrdinalIgnoreCase);

    public async Task<LlmCredentialTestResult> TestAsync(CredentialModel credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var template = credential.TemplateId.ToLowerInvariant();
        var configuredModel = credential.GetField("model");

        if (!IsLanguageModelTemplate(template))
        {
            return Result(LlmCredentialTestStatus.NotTestable, "These credentials are not for a language model.", [], configuredModel);
        }

        if (template == "aws_bedrock")
        {
            return Result(LlmCredentialTestStatus.NotTestable, "AWS credentials can only be checked by a real request: try the model from an agent.", [], configuredModel);
        }

        var apiKey = credential.GetField("api_key");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Result(LlmCredentialTestStatus.InvalidKey, "There is no API key in these credentials.", [], configuredModel);
        }

        // The address of the connection, as the model calls use it — or else the one its kind proposes.
        var address = ((string.IsNullOrWhiteSpace(credential.GetField("base_url")) ? null : credential.GetField("base_url")) ?? LlmClientFactory.DefaultBaseUrlOf(template))?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(address))
        {
            return Result(LlmCredentialTestStatus.Error, "These credentials name no address for the API: fill in the Base URL.", [], configuredModel);
        }

        using var request = BuildRequest(template, address, apiKey.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var client = httpClientFactory.CreateClient("agentivity-llm");
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Interpret(template, response.StatusCode, body, configuredModel);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(LlmCredentialTestStatus.Unreachable, "The provider did not answer in time. Check your connection and try again.", [], configuredModel);
        }
        catch (HttpRequestException)
        {
            return Result(LlmCredentialTestStatus.Unreachable, "The provider could not be reached. Check your connection and the address in these credentials.", [], configuredModel);
        }
    }

    // Only the path of the call is added to the address of the connection.
    private static HttpRequestMessage BuildRequest(string template, string baseUrl, string apiKey)
    {
        HttpRequestMessage request;

        switch (template)
        {
            case "anthropic":
                request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models?limit=100");
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                break;
            case "gemini":
                request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models?pageSize=200");
                request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                break;
            default:
                // OpenAI and the providers that speak its format (xAI, DeepSeek, ...).
                request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                break;
        }

        return request;
    }

    private static LlmCredentialTestResult Interpret(string template, HttpStatusCode status, string body, string? configuredModel)
    {
        switch ((int)status)
        {
            case >= 200 and < 300:
                var models = ParseModels(template, body);
                var available = models.Count == 0 || string.IsNullOrWhiteSpace(configuredModel) ? (bool?)null : models.Contains(configuredModel, StringComparer.OrdinalIgnoreCase);
                var message = available == false ? $"The key works, but the model '{configuredModel}' is not in the list it can use. Pick another model." : "The key works.";
                return Result(LlmCredentialTestStatus.Valid, message, models, configuredModel, available);
            case 401:
                return Result(LlmCredentialTestStatus.InvalidKey, "The provider rejected this key. Check that it is copied in full and has not been revoked.", [], configuredModel);
            case 400 when template == "gemini" && body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase):
                return Result(LlmCredentialTestStatus.InvalidKey, "The provider rejected this key. Check that it is copied in full and has not been revoked.", [], configuredModel);
            case 403:
                return Result(LlmCredentialTestStatus.Restricted, "The provider knows this key but does not let it list models. It may still work in a conversation: try it from an agent.", [], configuredModel);
            case 429:
                return Result(LlmCredentialTestStatus.RateLimited, "The provider is limiting requests for this key right now. Wait a moment and try again.", [], configuredModel);
            default:
                return Result(LlmCredentialTestStatus.Error, $"The provider answered with an unexpected error ({(int)status}). Try again in a moment.", [], configuredModel);
        }
    }

    private static List<string> ParseModels(string template, string body)
    {
        var models = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (template == "gemini")
            {
                if (root.TryGetProperty("models", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        // Only the models that answer chat requests, named without their "models/" prefix.
                        if (item.TryGetProperty("supportedGenerationMethods", out var methods) && !methods.EnumerateArray().Any(m => m.GetString() == "generateContent"))
                        {
                            continue;
                        }

                        if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text)
                        {
                            models.Add(StripPrefix(text, "models/"));
                        }
                    }
                }
            }
            else if (root.TryGetProperty("data", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } text)
                    {
                        models.Add(text);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // An answer that is not a model list still proved the key: it comes back without models.
        }

        return models;
    }

    private static string StripPrefix(string value, string prefix) => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static LlmCredentialTestResult Result(string status, string message, IReadOnlyList<string> models, string? configuredModel, bool? available = null) => new(status, message, models, configuredModel, available);
}
