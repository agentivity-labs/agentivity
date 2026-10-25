namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Lightweight notification record used internally by workflow engine components
/// to communicate state changes to <see cref="WorkflowRunEventBusPublisher"/>.
/// </summary>
public sealed record WorkflowRunStreamNotification(string Topic, string ResourceType, string Action, string Resource, string? ResourceId = null, IReadOnlyDictionary<string, object?>? Metadata = null);

public static class WorkflowRunStreamNotifications
{
    public static WorkflowRunStreamNotification RunUpdated(Guid runId, string reason, string? state = null)
    {
        var metadata = CreateMetadata(reason);
        if (!string.IsNullOrWhiteSpace(state))
            metadata["state"] = state;

        return new WorkflowRunStreamNotification(Topic: "execution", ResourceType: "run", Action: "updated", Resource: $"/workflow-runs/{runId}", ResourceId: runId.ToString(), Metadata: metadata);
    }

    public static WorkflowRunStreamNotification RunStartedWithAgUi(Guid runId, IReadOnlyDictionary<string, IReadOnlyList<string>> widgetBundles)
    {
        var metadata = CreateMetadata("started");
        metadata["state"] = WorkflowRunState.Running.ToString();
        if (widgetBundles.Count > 0)
            metadata["widget_bundles"] = widgetBundles;

        return new WorkflowRunStreamNotification(Topic: "execution", ResourceType: "run", Action: "updated", Resource: $"/workflow-runs/{runId}", ResourceId: runId.ToString(), Metadata: metadata);
    }

    public static WorkflowRunStreamNotification NodeUpdated(Guid runId, string nodeId, string? state = null)
    {
        var metadata = CreateMetadata("node_status_changed");
        metadata["nodeId"] = nodeId;
        if (!string.IsNullOrWhiteSpace(state))
            metadata["state"] = state;

        return new WorkflowRunStreamNotification(Topic: "execution", ResourceType: "node", Action: "updated", Resource: $"/workflow-runs/{runId}", ResourceId: nodeId, Metadata: metadata);
    }

    public static WorkflowRunStreamNotification ConnectionUpdated(Guid runId, string from, string fromPort, string to, string toPort) =>
        new(
            Topic: "execution",
            ResourceType: "connection",
            Action: "updated",
            Resource: $"/workflow-runs/{runId}",
            ResourceId: $"{from}:{fromPort}->{to}:{toPort}",
            Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["from"] = from,
                ["fromPort"] = fromPort,
                ["to"] = to,
                ["toPort"] = toPort,
                ["reason"] = "connection_traversed",
            }
        );

    public static WorkflowRunStreamNotification RunResumed(Guid runId) =>
        new(Topic: "execution", ResourceType: "run", Action: "updated", Resource: $"/workflow-runs/{runId}", ResourceId: runId.ToString(), Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["reason"] = "started", ["state"] = WorkflowRunState.Running.ToString() });

    public static WorkflowRunStreamNotification ChatThreadCreated(Guid runId, Guid threadId) =>
        new(Topic: "chat", ResourceType: "thread", Action: "created", Resource: $"/chat/contexts/{runId}/threads", ResourceId: threadId.ToString(), Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["threadId"] = threadId });

    public static WorkflowRunStreamNotification ChatThreadUpdated(Guid runId, Guid threadId, string reason) =>
        new(Topic: "chat", ResourceType: "thread", Action: "updated", Resource: $"/chat/contexts/{runId}/threads", ResourceId: threadId.ToString(), Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["threadId"] = threadId, ["reason"] = reason });

    public static WorkflowRunStreamNotification ChatMessageCreated(Guid runId, Guid threadId, Guid messageId) => ChatMessageChanged(runId, threadId, messageId, "created");

    public static WorkflowRunStreamNotification ChatMessageUpdated(Guid runId, Guid threadId, Guid messageId) => ChatMessageChanged(runId, threadId, messageId, "updated");

    public static WorkflowRunStreamNotification ChatMessageDeleted(Guid runId, Guid threadId, Guid messageId) => ChatMessageChanged(runId, threadId, messageId, "deleted");

    public static WorkflowRunStreamNotification InteractionRequestCreated(Guid runId, Guid requestId, string channelType) => InteractionRequestChanged(runId, requestId, channelType, "created");

    public static WorkflowRunStreamNotification InteractionRequestResolved(Guid runId, Guid requestId, string channelType) => InteractionRequestChanged(runId, requestId, channelType, "resolved");

    public static WorkflowRunStreamNotification InteractionRequestCanceled(Guid runId, Guid requestId, string channelType) => InteractionRequestChanged(runId, requestId, channelType, "canceled");

    private static WorkflowRunStreamNotification ChatMessageChanged(Guid runId, Guid threadId, Guid messageId, string action) =>
        new(Topic: "chat", ResourceType: "message", Action: action, Resource: $"/chat/contexts/{runId}/threads/{threadId}/messages", ResourceId: messageId.ToString(), Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["threadId"] = threadId, ["messageId"] = messageId });

    private static WorkflowRunStreamNotification InteractionRequestChanged(Guid runId, Guid requestId, string channelType, string action)
    {
        var normalizedChannelType = string.Equals(channelType, "forms", StringComparison.OrdinalIgnoreCase) ? "forms" : "interactions";

        var resource = normalizedChannelType == "forms" ? $"/forms/contexts/{runId}/requests" : $"/interactions/contexts/{runId}/requests?channelType={Uri.EscapeDataString(channelType)}";

        return new WorkflowRunStreamNotification(
            Topic: normalizedChannelType,
            ResourceType: "request",
            Action: action,
            Resource: resource,
            ResourceId: requestId.ToString(),
            Metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["requestId"] = requestId, ["channelType"] = channelType }
        );
    }

    private static Dictionary<string, object?> CreateMetadata(string reason) => new(StringComparer.OrdinalIgnoreCase) { ["reason"] = reason };
}
