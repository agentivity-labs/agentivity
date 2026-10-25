using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Maps <see cref="WorkflowRunStreamNotification"/> to a unified <see cref="RunEvent"/>
/// and publishes it on <see cref="IRunEventBus"/>.
/// </summary>
internal static class WorkflowRunEventBusPublisher
{
    public static void Publish(IRunEventBus bus, Guid runId, string workflowId, WorkflowRunStreamNotification notification, bool terminal = false, string? executionId = null, string? streamId = null)
    {
        var kind = MapKind(notification);

        // For node events, build a flat payload so RunEventMapper.ExtractStepName can find nodeId directly
        // without having to navigate into a nested Metadata dictionary.
        object payload = notification.Topic == "execution" && notification.ResourceType == "node" && notification.Metadata is not null ? BuildNodePayload(notification) : notification;

        var @event = new RunEvent(
            RunId: runId.ToString(),
            DefinitionId: workflowId,
            RunKind: RunKind.Workflow,
            Kind: kind,
            EventName: $"{notification.Topic}.{notification.ResourceType}.{notification.Action}",
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Payload: payload,
            Terminal: terminal,
            ExecutionId: executionId,
            // Routes the event to the execution's SSE channel (keyed by StreamId) — without it a workflow's
            // run/node lifecycle never reaches the client stream.
            StreamId: streamId
        );

        bus.Publish(@event);
    }

    private static object BuildNodePayload(WorkflowRunStreamNotification notification)
    {
        var nodeId = notification.Metadata!.GetValueOrDefault("nodeId") as string ?? notification.ResourceId ?? string.Empty;
        var state = notification.Metadata.GetValueOrDefault("state") as string ?? string.Empty;
        return new
        {
            nodeId,
            stepName = nodeId,
            state,
            resourceId = notification.ResourceId,
        };
    }

    private static RunEventKind MapKind(WorkflowRunStreamNotification n)
    {
        if (n.Topic == "execution" && n.ResourceType == "run")
        {
            var reason = n.Metadata?.GetValueOrDefault("reason") as string ?? string.Empty;
            return reason switch
            {
                "started" => RunEventKind.run_started,
                "completed" => RunEventKind.run_completed,
                "failed" => RunEventKind.run_failed,
                "cancelled" => RunEventKind.run_cancelled,
                _ => RunEventKind.run_updated,
            };
        }

        if (n.Topic == "execution" && n.ResourceType == "node")
        {
            var state = n.Metadata?.GetValueOrDefault("state") as string ?? string.Empty;
            return state switch
            {
                "Running" => RunEventKind.node_started,
                "Completed" => RunEventKind.node_completed,
                "Failed" => RunEventKind.node_failed,
                _ => RunEventKind.node_updated,
            };
        }

        if (n.Topic == "execution" && n.ResourceType == "connection")
            return RunEventKind.connection_updated;

        if (n.Topic == "chat" && n.ResourceType == "thread")
            return n.Action == "created" ? RunEventKind.chat_thread_created : RunEventKind.chat_thread_updated;

        if (n.ResourceType == "request")
            return n.Action switch
            {
                "created" => RunEventKind.interaction_created,
                "resolved" => RunEventKind.interaction_resolved,
                _ => RunEventKind.interaction_cancelled,
            };

        return RunEventKind.run_updated;
    }
}
