using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Interactions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;

public sealed class SlackService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SignatureValidityWindow = TimeSpan.FromMinutes(5);

    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public SlackService(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<SlackMessageReceipt> SendMessageAsync(string credentialsId, string channelIdOrName, string message, string? threadTimestamp = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelIdOrName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        SlackSettings settings = await ResolveSettingsAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(new Uri(settings.BaseUrl), "chat.postMessage");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        channel = channelIdOrName,
                        text = message,
                        thread_ts = threadTimestamp,
                        unfurl_links = false,
                        unfurl_media = false,
                    },
                    SerializerOptions
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.BotToken);

        using JsonDocument document = await SendSlackRequestAsync(settings.BotToken, httpRequest, cancellationToken).ConfigureAwait(false);
        SlackMessageReceipt receipt = ReadMessageReceipt(document.RootElement);

        _logger.LogInformation("Slack message sent to channel {Channel}", channelIdOrName);
        return receipt;
    }

    public async ValueTask<SlackMessageReceipt> UpdateMessageAsync(string credentialsId, string channelId, string messageTimestamp, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTimestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        SlackSettings settings = await ResolveSettingsAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(new Uri(settings.BaseUrl), "chat.update");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        channel = channelId,
                        ts = messageTimestamp,
                        text = message,
                        unfurl_links = false,
                        unfurl_media = false,
                    },
                    SerializerOptions
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };

        using JsonDocument document = await SendSlackRequestAsync(settings.BotToken, httpRequest, cancellationToken).ConfigureAwait(false);
        return ReadMessageReceipt(document.RootElement);
    }

    public async ValueTask DeleteMessageAsync(string credentialsId, string channelId, string messageTimestamp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTimestamp);

        SlackSettings settings = await ResolveSettingsAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(new Uri(settings.BaseUrl), "chat.delete");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(new { channel = channelId, ts = messageTimestamp }, SerializerOptions), Encoding.UTF8, "application/json") };

        using JsonDocument _ = await SendSlackRequestAsync(settings.BotToken, httpRequest, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SlackSearchResult>> SearchMessagesAsync(string credentialsId, string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        SlackSettings settings = await ResolveSettingsAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(new Uri(settings.BaseUrl), "search.messages");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?query={Uri.EscapeDataString(query)}");
        using JsonDocument document = await SendSlackRequestAsync(settings.BotToken, httpRequest, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("messages", out JsonElement messagesElement) || !messagesElement.TryGetProperty("matches", out JsonElement matchesElement) || matchesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SlackSearchResult>();
        foreach (JsonElement match in matchesElement.EnumerateArray())
        {
            string? channelId = match.TryGetProperty("channel", out JsonElement channelElement) && channelElement.TryGetProperty("id", out JsonElement channelIdElement) ? channelIdElement.GetString() : null;
            string? messageTimestamp = match.TryGetProperty("ts", out JsonElement tsElement) ? tsElement.GetString() : null;
            string? threadTimestamp = match.TryGetProperty("thread_ts", out JsonElement threadElement) ? threadElement.GetString() : messageTimestamp;
            string? userId = match.TryGetProperty("user", out JsonElement userElement) ? userElement.GetString() : null;
            string? text = match.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() : null;

            results.Add(new SlackSearchResult(channelId, messageTimestamp, threadTimestamp, userId, text));
        }

        return results;
    }

    public async ValueTask<string?> ValidateSignatureAsync(IEnumerable<string> credentialsIds, string timestamp, string signature, string rawBody, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        ArgumentNullException.ThrowIfNull(credentialsIds);

        if (!long.TryParse(timestamp, out long unixTimestamp))
        {
            return null;
        }

        var requestInstant = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        if (DateTimeOffset.UtcNow - requestInstant > SignatureValidityWindow)
        {
            return null;
        }

        foreach (string? credentialsId in credentialsIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? signingSecret = await GetSigningSecretAsync(credentialsId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(signingSecret))
            {
                continue;
            }

            string computed = ComputeSignature(signingSecret, timestamp, rawBody);
            if (string.Equals(computed, signature, StringComparison.Ordinal))
            {
                return credentialsId;
            }
        }

        return null;
    }

    private async Task<SlackSettings> ResolveSettingsAsync(string credentialsId, CancellationToken cancellationToken)
    {
        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string? botToken = credentials.GetField("bot_token")?.Trim();
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Slack credentials are missing the 'bot_token' field.");
        }

        string baseUrl = credentials.GetField("base_url", "https://slack.com/api/").Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        return new SlackSettings(botToken, baseUrl, credentials.GetField("signing_secret")?.Trim());
    }

    private async ValueTask<string?> GetSigningSecretAsync(string credentialsId, CancellationToken cancellationToken)
    {
        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        return credentials.GetField("signing_secret")?.Trim();
    }

    private static string ComputeSignature(string signingSecret, string timestamp, string rawBody)
    {
        string baseString = $"v0:{timestamp}:{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        return $"v0={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private async ValueTask<JsonDocument> SendSlackRequestAsync(string botToken, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);

        using HttpClient client = _httpClientFactory.CreateClient();
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Slack API request failed with status {(int)response.StatusCode}: {payload}");
        }

        var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("ok", out JsonElement okElement) || !okElement.GetBoolean())
        {
            string? error = document.RootElement.TryGetProperty("error", out JsonElement errorElement) ? errorElement.GetString() : "unknown_error";
            document.Dispose();
            throw new InvalidOperationException($"Slack API returned an error: {error}");
        }

        return document;
    }

    private static SlackMessageReceipt ReadMessageReceipt(JsonElement root)
    {
        string channelId = root.TryGetProperty("channel", out JsonElement channelElement) ? channelElement.GetString() ?? string.Empty : string.Empty;
        string messageTimestamp = root.TryGetProperty("ts", out JsonElement tsElement) ? tsElement.GetString() ?? string.Empty : string.Empty;

        return new SlackMessageReceipt(channelId, messageTimestamp, messageTimestamp);
    }

    public sealed record SlackMessageReceipt(string ChannelId, string MessageTimestamp, string ThreadTimestamp);

    public sealed record SlackSearchResult(string? ChannelId, string? MessageTimestamp, string? ThreadTimestamp, string? UserId, string? Text);

    private sealed record SlackSettings(string BotToken, string BaseUrl, string? SigningSecret);
}

internal static class SlackFormatting
{
    public static bool TryExtractReferencedRequestId(string? text, out Guid requestId)
    {
        requestId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string token in text.Split([' ', '\r', '\n', '\t', ',', ';', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = token.Trim();
            if (candidate.StartsWith("request:", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate["request:".Length..];
            }

            if (Guid.TryParse(candidate, out requestId))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildInteractionMessage(InteractionRequest request, string? mentionUsers)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(mentionUsers))
        {
            _ = builder.AppendLine(mentionUsers.Trim());
            _ = builder.AppendLine();
        }

        _ = builder.Append('*').Append(request.Title).AppendLine("*");

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            _ = builder.AppendLine(request.Description.Trim());
            _ = builder.AppendLine();
        }

        if (request.Fields.Count > 0)
        {
            _ = builder.AppendLine("Expected fields:");
            foreach (InteractionField field in request.Fields)
            {
                _ = builder.Append("• ").Append(field.Label).Append(" (").Append(field.Type).Append(field.Required ? ", required" : ", optional").AppendLine(")");
            }

            _ = builder.AppendLine();
        }

        _ = builder.Append("Request ID: ").AppendLine(request.Id.ToString());

        string? usageHint = BuildUsageHint(request);
        if (!string.IsNullOrWhiteSpace(usageHint))
        {
            _ = builder.AppendLine();
            _ = builder.AppendLine(usageHint);
        }

        if (request.Timeout is not null)
        {
            _ = builder.Append("Timeout: ").AppendLine(request.Timeout.ToString());
        }

        _ = builder.AppendLine();
        _ = builder.AppendLine("Reply directly in this thread to continue the workflow.");
        return builder.ToString().TrimEnd();
    }

    public static InteractionResponse? TryParseInteractionResponse(InteractionRequest request, InteractionChannelInboundMessage inboundMessage)
    {
        string? text = inboundMessage.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) || request.Kind == InteractionKind.Notification
            ? null
            : request.Kind switch
            {
                InteractionKind.Input => BuildInputResponse(request, inboundMessage, text),
                InteractionKind.Approval => BuildApprovalResponse(request, inboundMessage, text),
                _ => BuildInputResponse(request, inboundMessage, text),
            };
    }

    private static InteractionResponse BuildInputResponse(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, string text)
    {
        if (TryParseJsonObject(text, out Dictionary<string, object?>? jsonData, out InteractionOutcome? jsonOutcome))
        {
            return BuildResponse(request, inboundMessage, jsonOutcome ?? InteractionOutcome.Submitted, jsonData);
        }

        Dictionary<string, object?> data = TryParseKeyValueLines(text, request.Fields);
        if (data.Count == 0)
        {
            string targetField = request.Fields.FirstOrDefault()?.Name ?? "response";
            data[targetField] = text;
        }

        return BuildResponse(request, inboundMessage, InteractionOutcome.Submitted, data);
    }

    private static InteractionResponse? BuildApprovalResponse(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, string text)
    {
        if (TryParseJsonObject(text, out Dictionary<string, object?>? jsonData, out InteractionOutcome? jsonOutcome))
        {
            return BuildResponse(request, inboundMessage, jsonOutcome ?? InteractionOutcome.Submitted, jsonData);
        }

        int firstSpace = text.IndexOf(' ');
        string command = (firstSpace >= 0 ? text[..firstSpace] : text).Trim().ToLowerInvariant();
        string remainder = firstSpace >= 0 ? text[(firstSpace + 1)..].Trim() : string.Empty;

        InteractionOutcome outcome = command switch
        {
            "approve" or "approved" or "ok" or "yes" => InteractionOutcome.Approved,
            "reject" or "rejected" or "no" => InteractionOutcome.Rejected,
            "revise" or "revised" => InteractionOutcome.Revised,
            "submit" or "submitted" => InteractionOutcome.Submitted,
            _ => InteractionOutcome.Submitted,
        };

        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            if (outcome == InteractionOutcome.Revised)
            {
                if (request.Fields.Any(f => string.Equals(f.Name, "revised_content", StringComparison.OrdinalIgnoreCase)))
                {
                    data["revised_content"] = remainder;
                }
                else if (request.Fields.Any(f => string.Equals(f.Name, "revised_arguments_json", StringComparison.OrdinalIgnoreCase)))
                {
                    data["revised_arguments_json"] = remainder;
                }
                else
                {
                    data["feedback"] = remainder;
                }
            }
            else if (request.Fields.Any(f => string.Equals(f.Name, "response", StringComparison.OrdinalIgnoreCase)))
            {
                data["response"] = remainder;
            }
            else
            {
                data["feedback"] = remainder;
            }
        }

        return BuildResponse(request, inboundMessage, outcome, data);
    }

    private static InteractionResponse BuildResponse(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, InteractionOutcome outcome, IReadOnlyDictionary<string, object?> data) =>
        new()
        {
            RequestId = request.Id,
            Outcome = outcome,
            Data = data,
            Channel = inboundMessage.ChannelType,
            RespondedBy = inboundMessage.ExternalUserName ?? inboundMessage.ExternalUserId,
        };

    private static bool TryParseJsonObject(string text, out Dictionary<string, object?> data, out InteractionOutcome? outcome)
    {
        data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        outcome = null;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "outcome", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String && Enum.TryParse<InteractionOutcome>(property.Value.GetString(), true, out InteractionOutcome parsedOutcome))
                {
                    outcome = parsedOutcome;
                    continue;
                }

                data[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when property.Value.TryGetInt64(out long longValue) => longValue,
                    JsonValueKind.Number when property.Value.TryGetDouble(out double doubleValue) => doubleValue,
                    _ => property.Value.ToString(),
                };
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Dictionary<string, object?> TryParseKeyValueLines(string text, IReadOnlyList<InteractionField> fields)
    {
        var allowedFields = fields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            if (!allowedFields.Contains(key))
            {
                continue;
            }

            data[key] = value;
        }

        return data;
    }

    private static string? BuildUsageHint(InteractionRequest request) =>
        request.Kind switch
        {
            InteractionKind.Approval => "Reply with `approve`, `reject <feedback>`, or `revise <updated content>`.",
            InteractionKind.Input when request.Fields.Count == 1 => $"Reply with the value for `{request.Fields[0].Name}`.",
            InteractionKind.Input => "Reply with JSON or `field: value` lines matching the expected fields.",
            _ => null,
        };
}
