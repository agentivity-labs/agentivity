using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Interactions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;

public sealed class SlackInteractionWebhookProcessor
{
    private readonly InteractionStore _interactionStore;
    private readonly WorkflowEngine _workflowEngine;
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlackInteractionWebhookProcessor> _logger;

    public SlackInteractionWebhookProcessor(InteractionStore interactionStore, WorkflowEngine workflowEngine, CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<SlackInteractionWebhookProcessor> logger)
    {
        _interactionStore = interactionStore ?? throw new ArgumentNullException(nameof(interactionStore));
        _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<SlackWebhookProcessResult> ProcessAsync(string? timestamp, string? signature, string rawBody, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return SlackWebhookProcessResult.Failed("Slack request body is empty.");
        }

        using var document = JsonDocument.Parse(rawBody);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("type", out JsonElement typeElement) && string.Equals(typeElement.GetString(), "url_verification", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("challenge", out JsonElement challengeElement))
        {
            return SlackWebhookProcessResult.Challenge(challengeElement.GetString() ?? string.Empty);
        }

        var slackService = new SlackService(_credentialsService, _httpClientFactory, _logger);

        IReadOnlyList<Credentials> slackCredentials = await _credentialsService.GetByTemplateAsync("slack", cancellationToken).ConfigureAwait(false);
        string? matchingCredentialsId = await slackService.ValidateSignatureAsync(slackCredentials.Select(credentials => credentials.Id), timestamp ?? string.Empty, signature ?? string.Empty, rawBody, cancellationToken).ConfigureAwait(false);

        if (matchingCredentialsId is null)
        {
            return SlackWebhookProcessResult.Unauthorized();
        }

        if (!TryCreateSlackInboundMessage(root, out InteractionChannelInboundMessage? inboundMessage, out string? ignoredReason))
        {
            return SlackWebhookProcessResult.Ignored(ignoredReason);
        }

        var pendingRequests = _interactionStore
            .GetPendingRequests()
            .Where(request => string.Equals(request.ChannelType, "slack", StringComparison.OrdinalIgnoreCase))
            .Where(request =>
            {
                string? requestCredentialsId = GetInteractionRequestMetadataString(request.Metadata, "slack.credentials_id");
                return string.IsNullOrWhiteSpace(requestCredentialsId) || string.Equals(requestCredentialsId, matchingCredentialsId, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        var matches = new List<(InteractionRequest Request, InteractionResponse Response)>();
        foreach (InteractionRequest? request in pendingRequests)
        {
            InteractionResponse? response =
                request.Kind == InteractionKind.Message
                    ? await HumanInteractionChannelDispatcher.TryResolveInboundAsync(request, inboundMessage, cancellationToken).ConfigureAwait(false)
                    : await InteractionChannelDispatcher.TryResolveInboundAsync(request, inboundMessage, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                matches.Add((request, response));
            }
        }

        if (matches.Count == 0)
        {
            return SlackWebhookProcessResult.Ignored("no_matching_interaction");
        }

        if (matches.Count > 1)
        {
            return SlackWebhookProcessResult.Failed("Slack inbound message matched multiple pending interactions. Reply in the correct thread or include the explicit request id.");
        }

        (InteractionRequest Request, InteractionResponse Response) match = matches[0];
        await _interactionStore.SaveResponseAsync(match.Response, cancellationToken).ConfigureAwait(false);

        SuspendedRunContext? suspended = await _workflowEngine.ProvideInteractionResponseAsync(match.Request.RunId, match.Response, cancellationToken).ConfigureAwait(false);
        return suspended is null ? SlackWebhookProcessResult.Failed("Workflow is not in WaitingForInput state or has already been resumed.") : SlackWebhookProcessResult.Resumable(match.Request.RunId, match.Request.Id, suspended);
    }

    private static bool TryCreateSlackInboundMessage(JsonElement root, out InteractionChannelInboundMessage inboundMessage, out string ignoredReason)
    {
        inboundMessage = default!;
        ignoredReason = string.Empty;

        if (!root.TryGetProperty("type", out JsonElement typeElement) || !string.Equals(typeElement.GetString(), "event_callback", StringComparison.OrdinalIgnoreCase))
        {
            ignoredReason = "unsupported_slack_payload_type";
            return false;
        }

        if (!root.TryGetProperty("event", out JsonElement eventElement) || eventElement.ValueKind != JsonValueKind.Object)
        {
            ignoredReason = "missing_event";
            return false;
        }

        if (!eventElement.TryGetProperty("type", out JsonElement eventTypeElement) || !string.Equals(eventTypeElement.GetString(), "message", StringComparison.OrdinalIgnoreCase))
        {
            ignoredReason = "unsupported_event_type";
            return false;
        }

        if (eventElement.TryGetProperty("bot_id", out _) || (eventElement.TryGetProperty("subtype", out JsonElement subtypeElement) && !string.IsNullOrWhiteSpace(subtypeElement.GetString())))
        {
            ignoredReason = "bot_or_subtype_message";
            return false;
        }

        string? channelId = eventElement.TryGetProperty("channel", out JsonElement channelElement) ? channelElement.GetString() : null;
        string? userId = eventElement.TryGetProperty("user", out JsonElement userElement) ? userElement.GetString() : null;
        string? text = eventElement.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() : null;
        string? messageTimestamp = eventElement.TryGetProperty("ts", out JsonElement tsElement) ? tsElement.GetString() : null;
        string? threadTimestamp = eventElement.TryGetProperty("thread_ts", out JsonElement threadElement) ? threadElement.GetString() : messageTimestamp;

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(messageTimestamp))
        {
            ignoredReason = "missing_message_fields";
            return false;
        }

        inboundMessage = new InteractionChannelInboundMessage(
            ChannelType: "slack",
            ExternalChannelId: channelId,
            ExternalConversationId: threadTimestamp,
            ExternalMessageId: messageTimestamp,
            ExternalUserId: userId,
            ExternalUserName: userId,
            Text: text,
            Data: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["slack.channel"] = channelId,
                ["slack.thread_ts"] = threadTimestamp ?? string.Empty,
                ["slack.message_ts"] = messageTimestamp,
                ["slack.user"] = userId,
            }
        );

        return true;
    }

    private static string? GetInteractionRequestMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        return !metadata.TryGetValue(key, out object? value) || value is null
            ? null
            : value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement { ValueKind: JsonValueKind.String } jsonString => jsonString.GetString(),
                _ => value.ToString(),
            };
    }
}

public sealed record SlackWebhookProcessResult(SlackWebhookProcessOutcome Outcome, string? ChallengeText, string? Reason, string? Error, Guid? RunId, Guid? RequestId, SuspendedRunContext? SuspendedContext)
{
    public static SlackWebhookProcessResult Challenge(string challenge) => new(SlackWebhookProcessOutcome.Challenge, challenge, null, null, null, null, null);

    public static SlackWebhookProcessResult Unauthorized() => new(SlackWebhookProcessOutcome.Unauthorized, null, null, null, null, null, null);

    public static SlackWebhookProcessResult Ignored(string reason) => new(SlackWebhookProcessOutcome.Ignored, null, reason, null, null, null, null);

    public static SlackWebhookProcessResult Failed(string error) => new(SlackWebhookProcessOutcome.Failed, null, null, error, null, null, null);

    public static SlackWebhookProcessResult Resumable(Guid runId, Guid requestId, SuspendedRunContext suspendedContext) => new(SlackWebhookProcessOutcome.Resumable, null, null, null, runId, requestId, suspendedContext);
}

public enum SlackWebhookProcessOutcome
{
    Challenge,
    Unauthorized,
    Ignored,
    Failed,
    Resumable,
}
