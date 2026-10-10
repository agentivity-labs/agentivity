using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Interactions;

/// <summary>
/// Generic human interaction node for sending, waiting, receiving, updating, deleting, or searching messages through a connected channel.
/// </summary>
public sealed class HumanInteractionNode : BaseNode
{
    private readonly InteractionStore _store;

    public HumanInteractionNode(InteractionStore store, ILogger<HumanInteractionNode> logger)
        : base("interaction.human")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(logger);

        DefineMetadata("Human Interaction", "Performs a generic human interaction through the connected channel", "interaction");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("channel", "Channel", ResourcePortCategory.InteractionChannel, "Interaction channel (Slack, Email, Forms, etc.)");

        DefineChoice("operation", [("send", "Send"), ("send_and_wait", "Send and Wait"), ("receive", "Receive"), ("update", "Update"), ("delete", "Delete"), ("search", "Search")], "Interaction operation", "send", ui: new ArgumentUiMetadata { Group = "Operation", Order = 10 });
        DefineInput(
            "message",
            ArgumentType.Text,
            "Free-text message body",
            ui: new ArgumentUiMetadata
            {
                Group = "Content",
                Order = 20,
                Multiline = true,
                Rows = 6,
            }
        );
        DefineInput("message_id", ArgumentType.Text, "Existing message identifier for update/delete", ui: new ArgumentUiMetadata { Group = "Targets", Order = 30 });
        DefineInput("thread_id", ArgumentType.Text, "Conversation thread identifier", ui: new ArgumentUiMetadata { Group = "Targets", Order = 40 });
        DefineInput("conversation_id", ArgumentType.Text, "Conversation identifier", ui: new ArgumentUiMetadata { Group = "Targets", Order = 50 });
        DefineInput("query", ArgumentType.Text, "Search query", ui: new ArgumentUiMetadata { Group = "Search", Order = 60 });
        DefineInput("timeout_minutes", ArgumentType.Number, "Optional wait timeout in minutes (0 = channel default)", 0, ui: new ArgumentUiMetadata { Group = "Settings", Order = 70 });

        DefineOutput("success", ArgumentType.Boolean, "Whether the interaction operation succeeded");
        DefineOutput("outcome", ArgumentType.Text, "Interaction outcome");
        DefineOutput("response_text", ArgumentType.Text, "Free-text response received from the human or channel");
        DefineOutput("responded_by", ArgumentType.Text, "Who responded");
        DefineOutput("message_id", ArgumentType.Text, "Message identifier");
        DefineOutput("thread_id", ArgumentType.Text, "Thread identifier");
        DefineOutput("conversation_id", ArgumentType.Text, "Conversation identifier");
        DefineOutput("messages", ArgumentType.Text, "Search results or returned messages as JSON");
        DefineOutput("error", ArgumentType.Text, "Error message if the operation failed");

        DefineIcon(NodeIcon.Material(0xe0ca, "#14b8a6"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string responseKey = $"__interaction_response_{config.Node.Id}";
        if (config.State.TryGetValue(responseKey, out object? existing) && existing is InteractionResponse resumedResponse)
        {
            _ = config.State.Remove(responseKey);
            return MapResponse(resumedResponse);
        }

        ResourceBinding binding = ResourceResolver.Require(config, "channel", ResourcePortCategory.InteractionChannel);
        var channelTypeFromNode = NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(binding.SourceNode.NodeType)?.ChannelType;
        var channelType = channelTypeFromNode ?? binding.GetString("channel_type") ?? throw new InvalidOperationException($"Cannot determine channel type for resource node '{binding.SourceNode.NodeType}'.");
        var channel = new ChannelBinding(ChannelType: channelType, Credentials: binding.GetString("credentials"), Respondent: binding.GetString("respondent"), Properties: binding);

        InteractionRequest request = BuildRequest(config, services, channel, args);
        HumanInteractionDispatchResult dispatch = await HumanInteractionChannelDispatcher.ExecuteAsync(channel, request, cancellationToken).ConfigureAwait(false);

        if (dispatch.AwaitResponse)
        {
            await _store.SaveRequestAsync(dispatch.Request, cancellationToken).ConfigureAwait(false);
            throw new WorkflowSuspendedException(dispatch.Request);
        }

        return MapResponse(
            dispatch.Response
                ?? new InteractionResponse
                {
                    RequestId = dispatch.Request.Id,
                    Outcome = InteractionOutcome.Submitted,
                    Success = true,
                    MessageId = dispatch.Request.MessageId,
                    ThreadId = dispatch.Request.ThreadId,
                    ConversationId = dispatch.Request.ConversationId,
                }
        );
    }

    private static InteractionRequest BuildRequest(NodeConfig config, WorkflowRunServices services, ChannelBinding channel, ArgumentInputValues args)
    {
        HumanInteractionOperation operation = ParseOperation(args.GetText("operation"));
        int timeoutMinutes = Math.Max(0, args.GetInt("timeout_minutes") ?? 0);
        int channelTimeoutMinutes = channel.Properties.GetInt("timeout_minutes") ?? 0;
        int effectiveTimeoutMinutes = timeoutMinutes > 0 ? timeoutMinutes : channelTimeoutMinutes;

        ValidateInputs(operation, args);

        return new InteractionRequest
        {
            Id = Guid.NewGuid(),
            RunId = services.RunId,
            NodeId = config.Node.Id,
            Kind = InteractionKind.Message,
            Operation = operation,
            ChannelType = channel.ChannelType,
            Title = args.GetText("message") ?? operation.ToString(),
            Message = NullIfWhiteSpace(args.GetText("message")),
            MessageId = NullIfWhiteSpace(args.GetText("message_id")),
            ThreadId = NullIfWhiteSpace(args.GetText("thread_id")),
            ConversationId = NullIfWhiteSpace(args.GetText("conversation_id")),
            Query = NullIfWhiteSpace(args.GetText("query")),
            Respondent = channel.Respondent,
            ExecutionId = services.ExecutionId,
            StreamId = services.StreamId,
            Timeout = effectiveTimeoutMinutes > 0 ? TimeSpan.FromMinutes(effectiveTimeoutMinutes) : null,
            Metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ValidateInputs(HumanInteractionOperation operation, ArgumentInputValues args)
    {
        string? message = args.GetText("message");
        string? messageId = args.GetText("message_id");
        string? query = args.GetText("query");

        switch (operation)
        {
            case HumanInteractionOperation.Send:
            case HumanInteractionOperation.SendAndWait:
                if (string.IsNullOrWhiteSpace(message))
                {
                    throw new InvalidOperationException("The human interaction node requires 'message' for send operations.");
                }
                break;

            case HumanInteractionOperation.Update:
                if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(message))
                {
                    throw new InvalidOperationException("The human interaction node requires both 'message_id' and 'message' for update operations.");
                }
                break;

            case HumanInteractionOperation.Delete:
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    throw new InvalidOperationException("The human interaction node requires 'message_id' for delete operations.");
                }
                break;

            case HumanInteractionOperation.Search:
                if (string.IsNullOrWhiteSpace(query))
                {
                    throw new InvalidOperationException("The human interaction node requires 'query' for search operations.");
                }
                break;
        }
    }

    private static HumanInteractionOperation ParseOperation(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "send" => HumanInteractionOperation.Send,
            "send_and_wait" => HumanInteractionOperation.SendAndWait,
            "receive" => HumanInteractionOperation.Receive,
            "update" => HumanInteractionOperation.Update,
            "delete" => HumanInteractionOperation.Delete,
            "search" => HumanInteractionOperation.Search,
            _ => HumanInteractionOperation.Send,
        };

    private static NodeResult MapResponse(InteractionResponse response)
    {
        return NodeResult
            .Create()
            .AddBoolean("success", response.Success)
            .AddText("outcome", response.Outcome.ToString())
            .AddText("response_text", response.ResponseText ?? string.Empty)
            .AddText("responded_by", response.RespondedBy ?? string.Empty)
            .AddText("message_id", response.MessageId ?? string.Empty)
            .AddText("thread_id", response.ThreadId ?? string.Empty)
            .AddText("conversation_id", response.ConversationId ?? string.Empty)
            .AddText("messages", response.Messages.Count > 0 ? JsonSerializer.Serialize(response.Messages) : string.Empty)
            .AddText("error", response.Error ?? string.Empty);
    }
}
