using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Interactions;

/// <summary>
/// Slack channel resource node for Slack-based user interactions.
/// </summary>
public sealed class SlackChannelNode : ResourceNode, IInteractionChannel, IHumanInteractionChannel, IAgentHumanLoopCapabilityProvider
{
    private const string AskHumanToolName = "ask_human";
    private readonly SlackService _slackService;
    private static readonly HumanInteractionOperation[] SupportedOperations = [HumanInteractionOperation.Send, HumanInteractionOperation.SendAndWait, HumanInteractionOperation.Receive, HumanInteractionOperation.Update, HumanInteractionOperation.Delete, HumanInteractionOperation.Search];

    public string ChannelType => "slack";

    public SlackChannelNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<SlackChannelNode> logger)
        : base("ai.interactions.slack")
    {
        ArgumentNullException.ThrowIfNull(credentialsService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _slackService = new SlackService(credentialsService, httpClientFactory, logger ?? throw new ArgumentNullException(nameof(logger)));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.InteractionChannel);

        DefineMetadata("Slack Channel", "Slack workspace channel for delivering interaction requests and collecting responses.", "ai.interactions");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Channel", ResourcePortCategory.InteractionChannel, maxConnections: -1);

        DefineCredentialsInput("credentials", "slack", "Slack API credentials (Bot token)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput("default_channel", ArgumentType.Text, "Default Slack channel ID for HIL interactions", ui: new ArgumentUiMetadata { Group = "Channel", Order = 30 });
        DefineInput("mention_users", ArgumentType.Text, "Users to mention (@user1, @user2)", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 40 });
        DefineInput("allowed_user_ids", ArgumentType.Text, "Optional Slack user IDs allowed to answer (comma-separated)", "", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 50 });
        DefineInput("send_initial_message", ArgumentType.Boolean, "Send the initial HIL prompt to Slack before waiting for a reply", true, ui: new ArgumentUiMetadata { Group = "Settings", Order = 60 });
        DefineInput("timeout_minutes", ArgumentType.Number, "Response timeout in minutes (0 = no timeout)", 60, ui: new ArgumentUiMetadata { Group = "Settings", Order = 70 });

        DefineIcon(NodeIcon.Embedded("slack_channel"));
    }

    public async ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["slack.credentials_id"] = channel.Properties.RequireString("credentials"),
            ["slack.channel_id"] = channel.Properties.RequireString("default_channel"),
            ["slack.allowed_user_ids"] = ParseDelimitedValues(channel.Properties.GetString("allowed_user_ids")),
            ["slack.send_initial_message"] = channel.Properties.GetBool("send_initial_message") ?? true,
        };

        if (!(channel.Properties.GetBool("send_initial_message") ?? true))
        {
            return new InteractionChannelDispatchResult(false, metadata);
        }

        string message = SlackFormatting.BuildInteractionMessage(request, channel.Properties.GetString("mention_users"));
        SlackService.SlackMessageReceipt receipt = await _slackService.SendMessageAsync(credentialsId: channel.Properties.RequireString("credentials"), channelIdOrName: channel.Properties.RequireString("default_channel"), message, threadTimestamp: null, cancellationToken).ConfigureAwait(false);

        metadata["slack.channel_id"] = receipt.ChannelId;
        metadata["slack.message_ts"] = receipt.MessageTimestamp;
        metadata["slack.thread_ts"] = receipt.ThreadTimestamp;

        return new InteractionChannelDispatchResult(true, metadata);
    }

    public ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(inboundMessage.ChannelType, "slack", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<InteractionResponse?>(null);
        }

        if (SlackFormatting.TryExtractReferencedRequestId(inboundMessage.Text, out Guid referencedRequestId) && referencedRequestId != request.Id)
        {
            return ValueTask.FromResult<InteractionResponse?>(null);
        }

        string? channelId = GetMetadataString(request.Metadata, "slack.channel_id");
        if (!string.IsNullOrWhiteSpace(channelId) && !string.Equals(channelId, inboundMessage.ExternalChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<InteractionResponse?>(null);
        }

        if (TryGetAllowedUserIds(request.Metadata, out HashSet<string>? allowedUserIds) && allowedUserIds.Count > 0 && (string.IsNullOrWhiteSpace(inboundMessage.ExternalUserId) || !allowedUserIds.Contains(inboundMessage.ExternalUserId)))
        {
            return ValueTask.FromResult<InteractionResponse?>(null);
        }

        string? threadTs = GetMetadataString(request.Metadata, "slack.thread_ts");
        if (!string.IsNullOrWhiteSpace(threadTs))
        {
            string? inboundThreadTs = inboundMessage.ExternalConversationId ?? inboundMessage.ExternalMessageId;
            if (!string.Equals(threadTs, inboundThreadTs, StringComparison.Ordinal))
            {
                if (request.Kind != InteractionKind.Message)
                {
                    return ValueTask.FromResult<InteractionResponse?>(null);
                }
            }
        }

        return request.Kind == InteractionKind.Message ? ValueTask.FromResult(BuildFreeTextResponse(request, inboundMessage)) : ValueTask.FromResult(SlackFormatting.TryParseInteractionResponse(request, inboundMessage));
    }

    public IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations() => SupportedOperations;

    // This channel's one tool is inherently a request-and-wait (SendAndWait) — there is no
    // restricted "send only" shape to fall back to. When the requesting agent isn't allowed to
    // wait for a reply at all, offer no tool here rather than one that only pretends not to block.
    public AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true) =>
        !allowsAskMode
            ? AgentHumanLoopCapabilities.None
            : new(
            [
                AgentHumanLoopToolDefinitionJson.Create(
                    name: AskHumanToolName,
                    description: "Ask a human a concise follow-up question in Slack when information is missing.",
                    inputSchema: """
                    {
                      "type": "object",
                      "properties": {
                        "question": { "type": "string" },
                        "context": { "type": "string" }
                      },
                      "required": ["question"],
                      "additionalProperties": false
                    }
                    """,
                    interactionKind: InteractionKind.Input,
                    operation: HumanInteractionOperation.SendAndWait,
                    resultMode: AgentHumanLoopResultMode.FreeText,
                    defaultTitle: "Clarification needed",
                    questionArgumentName: "question",
                    contextArgumentName: "context"
                ),
            ],
            [
                "A Slack human-loop channel is connected.",
                $"`{AskHumanToolName}` is the channel-native way to request clarification, confirmation, or any other human input in Slack.",
                $"When you need human input, call `{AskHumanToolName}` instead of asking follow-up questions in normal assistant text.",
                "Write the question in concise, human-readable Slack wording.",
                "Use the optional context only for short background that helps the human answer more precisely.",
            ]
        );

    public async ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> metadata = CreateBaseMetadata(channel, request);

        switch (request.Operation)
        {
            case HumanInteractionOperation.Send:
            case HumanInteractionOperation.SendAndWait:
            {
                SlackService.SlackMessageReceipt receipt = await _slackService
                    .SendMessageAsync(channel.Properties.RequireString("credentials"), metadata["slack.channel_id"]?.ToString() ?? channel.Properties.RequireString("default_channel"), request.Message ?? string.Empty, request.ThreadId, cancellationToken)
                    .ConfigureAwait(false);

                metadata["slack.channel_id"] = receipt.ChannelId;
                metadata["slack.message_ts"] = receipt.MessageTimestamp;
                metadata["slack.thread_ts"] = receipt.ThreadTimestamp;

                return request.Operation == HumanInteractionOperation.SendAndWait
                    ? HumanInteractionExecutionResult.Waiting(metadata)
                    : HumanInteractionExecutionResult.Completed(
                        new InteractionResponse
                        {
                            RequestId = request.Id,
                            Outcome = InteractionOutcome.Submitted,
                            Success = true,
                            MessageId = receipt.MessageTimestamp,
                            ThreadId = receipt.ThreadTimestamp,
                            ConversationId = receipt.ChannelId,
                            Channel = channel.ChannelType,
                            Metadata = metadata,
                        },
                        metadata
                    );
            }

            case HumanInteractionOperation.Receive:
                return HumanInteractionExecutionResult.Waiting(metadata);

            case HumanInteractionOperation.Update:
            {
                SlackService.SlackMessageReceipt receipt = await _slackService
                    .UpdateMessageAsync(
                        channel.Properties.RequireString("credentials"),
                        metadata["slack.channel_id"]?.ToString() ?? channel.Properties.RequireString("default_channel"),
                        request.MessageId ?? throw new InvalidOperationException("Slack update requires a message id."),
                        request.Message ?? throw new InvalidOperationException("Slack update requires a message."),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                metadata["slack.message_ts"] = receipt.MessageTimestamp;
                metadata["slack.thread_ts"] = receipt.ThreadTimestamp;

                return HumanInteractionExecutionResult.Completed(
                    new InteractionResponse
                    {
                        RequestId = request.Id,
                        Outcome = InteractionOutcome.Submitted,
                        Success = true,
                        MessageId = receipt.MessageTimestamp,
                        ThreadId = receipt.ThreadTimestamp,
                        ConversationId = receipt.ChannelId,
                        Channel = channel.ChannelType,
                        Metadata = metadata,
                    },
                    metadata
                );
            }

            case HumanInteractionOperation.Delete:
                await _slackService
                    .DeleteMessageAsync(
                        channel.Properties.RequireString("credentials"),
                        metadata["slack.channel_id"]?.ToString() ?? channel.Properties.RequireString("default_channel"),
                        request.MessageId ?? throw new InvalidOperationException("Slack delete requires a message id."),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return HumanInteractionExecutionResult.Completed(
                    new InteractionResponse
                    {
                        RequestId = request.Id,
                        Outcome = InteractionOutcome.Submitted,
                        Success = true,
                        MessageId = request.MessageId,
                        ThreadId = request.ThreadId,
                        ConversationId = request.ConversationId,
                        Channel = channel.ChannelType,
                        Metadata = metadata,
                    },
                    metadata
                );

            case HumanInteractionOperation.Search:
            {
                IReadOnlyList<SlackService.SlackSearchResult> results = await _slackService.SearchMessagesAsync(channel.Properties.RequireString("credentials"), request.Query ?? throw new InvalidOperationException("Slack search requires a query."), cancellationToken).ConfigureAwait(false);

                var messages = results
                    .Select(result => new InteractionMessage
                    {
                        MessageId = result.MessageTimestamp,
                        ThreadId = result.ThreadTimestamp,
                        ConversationId = result.ChannelId,
                        AuthorId = result.UserId,
                        Text = result.Text,
                    })
                    .ToList();

                return HumanInteractionExecutionResult.Completed(
                    new InteractionResponse
                    {
                        RequestId = request.Id,
                        Outcome = InteractionOutcome.Submitted,
                        Success = true,
                        Messages = messages,
                        Channel = channel.ChannelType,
                        Metadata = metadata,
                        ResponseText = messages.Count == 0 ? string.Empty : string.Join(Environment.NewLine + Environment.NewLine, messages.Select(message => message.Text)),
                    },
                    metadata
                );
            }

            default:
                throw new InvalidOperationException($"Unsupported Slack human interaction operation '{request.Operation}'.");
        }
    }

    private static Dictionary<string, object?> CreateBaseMetadata(ChannelBinding channel, InteractionRequest request)
    {
        string channelId = string.IsNullOrWhiteSpace(request.ConversationId) ? channel.Properties.RequireString("default_channel") : request.ConversationId;

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["slack.credentials_id"] = channel.Properties.RequireString("credentials"),
            ["slack.channel_id"] = channelId,
            ["slack.allowed_user_ids"] = ParseDelimitedValues(channel.Properties.GetString("allowed_user_ids")),
            ["slack.send_initial_message"] = channel.Properties.GetBool("send_initial_message") ?? true,
        };

        if (!string.IsNullOrWhiteSpace(request.ThreadId))
        {
            metadata["slack.thread_ts"] = request.ThreadId;
        }

        if (!string.IsNullOrWhiteSpace(request.MessageId))
        {
            metadata["slack.message_ts"] = request.MessageId;
        }

        return metadata;
    }

    private static InteractionResponse BuildFreeTextResponse(InteractionRequest request, InteractionChannelInboundMessage inboundMessage) =>
        new()
        {
            RequestId = request.Id,
            Outcome = InteractionOutcome.Submitted,
            Success = true,
            ResponseText = inboundMessage.Text?.Trim(),
            Channel = inboundMessage.ChannelType,
            RespondedBy = inboundMessage.ExternalUserName ?? inboundMessage.ExternalUserId,
            MessageId = inboundMessage.ExternalMessageId,
            ThreadId = inboundMessage.ExternalConversationId ?? inboundMessage.ExternalMessageId,
            ConversationId = inboundMessage.ExternalChannelId,
        };

    private static string[] ParseDelimitedValues(string? rawValue) => string.IsNullOrWhiteSpace(rawValue) ? [] : rawValue.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

    private static bool TryGetAllowedUserIds(IReadOnlyDictionary<string, object?> metadata, out HashSet<string> allowedUserIds)
    {
        allowedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!metadata.TryGetValue("slack.allowed_user_ids", out object? rawValue) || rawValue is null)
        {
            return false;
        }

        switch (rawValue)
        {
            case string text when !string.IsNullOrWhiteSpace(text):
                foreach (string value in ParseDelimitedValues(text))
                {
                    _ = allowedUserIds.Add(value);
                }
                break;

            case IEnumerable<object?> values:
                foreach (string? value in values.Select(v => v?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    _ = allowedUserIds.Add(value!);
                }
                break;

            case JsonElement { ValueKind: JsonValueKind.Array } jsonArray:
                foreach (string? value in jsonArray.EnumerateArray().Select(item => item.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    _ = allowedUserIds.Add(value);
                }
                break;
        }

        return allowedUserIds.Count > 0;
    }

    private static string? GetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        return !metadata.TryGetValue(key, out object? rawValue) || rawValue is null
            ? null
            : rawValue switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement { ValueKind: JsonValueKind.String } jsonString => jsonString.GetString(),
                _ => rawValue.ToString(),
            };
    }
}
