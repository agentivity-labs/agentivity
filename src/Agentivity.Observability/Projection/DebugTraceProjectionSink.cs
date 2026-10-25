using System.Collections.Concurrent;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Contracts.Events.Agent;
using Agentivity.Observability.Contracts.Events.AgentControl;
using Agentivity.Observability.Contracts.Events.Llm;
using Agentivity.Observability.Contracts.Events.Node;
using Agentivity.Observability.Contracts.Events.Run;
using Agentivity.Observability.Contracts.Events.Tools;

namespace Agentivity.Observability.Projection;

public sealed class DebugTraceProjectionSink : IAgentTraceSink
{
    private readonly ConcurrentDictionary<string, ProjectedDebugTraceItem> _traces = new(StringComparer.Ordinal);
    private readonly IDebugTraceProjectionTarget? _target;

    public DebugTraceProjectionSink(IDebugTraceProjectionTarget? target = null)
    {
        _target = target;
    }

    public ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(envelope.Correlation.NodeExecutionId))
        {
            return ValueTask.CompletedTask;
        }

        var key = BuildKey(envelope.Correlation.RunId, envelope.Correlation.NodeExecutionId);
        var eventItem = ProjectEvent(envelope);
        var root = _traces.AddOrUpdate(key, _ => CreateRoot(envelope, eventItem), (_, existing) => AppendChild(existing, envelope, eventItem));

        _target?.UpdateNodeTrace(envelope.Correlation.RunId, envelope.Correlation.NodeExecutionId!, root);
        return ValueTask.CompletedTask;
    }

    public bool TryGetTrace(Guid runId, string nodeId, out ProjectedDebugTraceItem? trace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var found = _traces.TryGetValue(BuildKey(runId, nodeId), out var value);
        trace = value;
        return found;
    }

    private static string BuildKey(Guid runId, string nodeId) => $"{runId:N}:{nodeId}";

    private static ProjectedDebugTraceItem CreateRoot(AgentTelemetryEnvelope envelope, ProjectedDebugTraceItem child)
    {
        var group = CreateGroupNode(envelope, child);
        return new(
            Kind: "observability",
            Name: "Structured observability",
            Message: $"Structured telemetry projection for node '{envelope.Correlation.NodeExecutionId}'.",
            Status: group.Status,
            Fields:
            [
                new ProjectedDebugTraceField("run_id", envelope.Correlation.RunId.ToString("D")),
                new ProjectedDebugTraceField("entity_id", envelope.Correlation.EntityId),
                new ProjectedDebugTraceField("node_execution_id", envelope.Correlation.NodeExecutionId ?? string.Empty),
                new ProjectedDebugTraceField("node_run_id", envelope.Correlation.NodeRunId ?? string.Empty),
            ],
            Children: [group]
        );
    }

    private static ProjectedDebugTraceItem AppendChild(ProjectedDebugTraceItem existing, AgentTelemetryEnvelope envelope, ProjectedDebugTraceItem child)
    {
        var children = existing.Children?.ToList() ?? [];
        var groupIndex = children.FindIndex(candidate => string.Equals(candidate.Kind, child.Kind, StringComparison.Ordinal));

        if (groupIndex < 0)
        {
            children.Add(CreateGroupNode(envelope, child));
        }
        else
        {
            var existingGroup = children[groupIndex];
            children[groupIndex] = existingGroup with { Status = MergeStatus(existingGroup.Status, child.Status), Children = AppendGroupedChild(existingGroup, child) };
        }

        return existing with
        {
            Status = children.Aggregate(ProjectedDebugTraceStatus.Info, (current, candidate) => MergeStatus(current, candidate.Status)),
            Children = children,
        };
    }

    private static ProjectedDebugTraceItem CreateGroupNode(AgentTelemetryEnvelope envelope, ProjectedDebugTraceItem child) =>
        new(Kind: child.Kind, Name: ResolveGroupName(child.Kind), Message: ResolveGroupMessage(envelope, child.Kind), Status: child.Status, Children: AppendGroupedChild(null, child));

    private static ProjectedDebugTraceItem ProjectEvent(AgentTelemetryEnvelope envelope)
    {
        var (message, status, fields) = envelope.Payload switch
        {
            RunStartedEvent payload => ($"Workflow '{payload.WorkflowId}' started.", ProjectedDebugTraceStatus.Info, CreateFields(("workflow_id", payload.WorkflowId))),
            RunCompletedEvent payload => ($"Workflow '{payload.WorkflowId}' completed with status '{payload.Status}'.", ProjectedDebugTraceStatus.Info, CreateFields(("duration_ms", payload.DurationMs.ToString()))),
            RunFailedEvent payload => ($"Workflow failed: {payload.ErrorMessage}", ProjectedDebugTraceStatus.Error, CreateFields(("error", payload.ErrorMessage))),
            NodeStartedEvent payload => ($"Node '{payload.NodeId}' ({payload.NodeType}) started.", ProjectedDebugTraceStatus.Info, CreateFields(("node_type", payload.NodeType))),
            NodeCompletedEvent payload => ($"Node '{payload.NodeId}' completed.", ProjectedDebugTraceStatus.Info, CreateFields(("duration_ms", payload.DurationMs.ToString()))),
            NodeFailedEvent payload => ($"Node '{payload.NodeId}' failed: {payload.ErrorMessage}", ProjectedDebugTraceStatus.Error, CreateFields(("error", payload.ErrorMessage))),
            AgentStartedEvent payload => ($"Agent started in mode '{payload.AgentMode}'.", ProjectedDebugTraceStatus.Info, CreateFields(("agent_mode", payload.AgentMode))),
            AgentCompletedEvent payload => ($"Agent completed after {payload.IterationCount} iteration(s).", ProjectedDebugTraceStatus.Info, CreateFields(("iterations", payload.IterationCount.ToString()), ("total_tokens", payload.TotalTokens.ToString()))),
            AgentFailedEvent payload => ($"Agent failed: {payload.ErrorMessage}", ProjectedDebugTraceStatus.Error, CreateFields(("error", payload.ErrorMessage))),
            LlmCallStartedEvent payload => ($"LLM call started: {payload.Provider}/{payload.Model} ({payload.OperationKind}).", ProjectedDebugTraceStatus.Info, CreateFields(("provider", payload.Provider), ("model", payload.Model), ("operation", payload.OperationKind))),
            LlmCallCompletedEvent payload => (
                $"LLM call completed with finish reason '{payload.FinishReason ?? "unknown"}'.",
                payload.Succeeded ? ProjectedDebugTraceStatus.Info : ProjectedDebugTraceStatus.Error,
                CreateFields(
                    ("provider", payload.Provider),
                    ("model", payload.Model),
                    ("operation", payload.OperationKind),
                    ("duration_ms", payload.DurationMs.ToString()),
                    ("total_tokens", payload.TotalTokens.ToString()),
                    ("finish_reason", payload.FinishReason ?? string.Empty),
                    ("error", payload.ErrorMessage ?? string.Empty)
                )
            ),
            ToolCallStartedEvent payload => ($"Tool '{payload.ToolName}' started.", ProjectedDebugTraceStatus.Info, CreateFields(("tool_name", payload.ToolName), ("arguments_hash", payload.ArgumentsHash ?? string.Empty))),
            ToolCallCompletedEvent payload => (
                $"Tool '{payload.ToolName}' {(payload.Succeeded ? "completed" : "failed")}.",
                payload.Succeeded ? ProjectedDebugTraceStatus.Info : ProjectedDebugTraceStatus.Error,
                CreateFields(("tool_name", payload.ToolName), ("duration_ms", payload.DurationMs.ToString()), ("reused_from_state", payload.ReusedFromState.ToString()), ("error", payload.ErrorMessage ?? string.Empty))
            ),
            HumanInterventionRequestedEvent payload => ($"Human intervention requested: {payload.Question}", ProjectedDebugTraceStatus.Warning, CreateFields(("question_id", payload.QuestionId), ("channel_type", payload.ChannelType ?? string.Empty))),
            HumanInterventionCompletedEvent payload => ($"Human intervention completed with outcome '{payload.Outcome}'.", ProjectedDebugTraceStatus.Info, CreateFields(("question_id", payload.QuestionId), ("duration_ms", payload.DurationMs.ToString()), ("outcome", payload.Outcome))),
            ReplanTriggeredEvent payload => (
                $"Replan triggered: {payload.Reason}",
                ProjectedDebugTraceStatus.Warning,
                CreateFields(
                    ("trigger", payload.Trigger),
                    ("revision_from", payload.RevisionFrom.ToString()),
                    ("revision_to", payload.RevisionTo.ToString()),
                    ("scope", payload.Scope ?? string.Empty),
                    ("preserved", payload.PreservedTaskIds is { Count: > 0 } ? string.Join(",", payload.PreservedTaskIds) : string.Empty),
                    ("affected", payload.AffectedTaskIds is { Count: > 0 } ? string.Join(",", payload.AffectedTaskIds) : string.Empty),
                    ("frontier", payload.ReplanFrontierTaskIds is { Count: > 0 } ? string.Join(",", payload.ReplanFrontierTaskIds) : string.Empty),
                    ("graph_revision_count", payload.GraphRevisionCount?.ToString() ?? string.Empty),
                    ("graph_task_count", payload.GraphTaskCount?.ToString() ?? string.Empty)
                )
            ),
            ErrorRaisedEvent payload => (
                $"Error raised: {payload.ErrorMessage}",
                payload.IsTerminal ? ProjectedDebugTraceStatus.Error : ProjectedDebugTraceStatus.Warning,
                CreateFields(("scope_type", payload.ScopeType), ("scope_id", payload.ScopeId), ("error_code", payload.ErrorCode ?? string.Empty))
            ),
            _ => ($"Telemetry event '{envelope.EventName}' captured.", ProjectedDebugTraceStatus.Info, CreateFields(("event_name", envelope.EventName))),
        };

        return new ProjectedDebugTraceItem(Kind: ResolveGroupKind(envelope.Kind), Name: envelope.EventName, Message: message, Status: status, Fields: fields, StepId: envelope.Correlation.StepId, SpanId: envelope.Correlation.SpanId, CorrelationKey: ResolveCorrelationKey(envelope));
    }

    private static IReadOnlyList<ProjectedDebugTraceItem> AppendGroupedChild(ProjectedDebugTraceItem? group, ProjectedDebugTraceItem child)
    {
        var children = group?.Children?.ToList() ?? [];
        if (string.IsNullOrWhiteSpace(child.StepId))
        {
            children.Add(child);
            return children;
        }

        var stepIndex = children.FindIndex(candidate => string.Equals(candidate.Kind, $"{child.Kind}-step", StringComparison.Ordinal) && string.Equals(candidate.StepId, child.StepId, StringComparison.Ordinal));

        if (stepIndex < 0)
        {
            children.Add(CreateStepNode(child));
            return children;
        }

        var existingStep = children[stepIndex];
        children[stepIndex] = existingStep with { Status = MergeStatus(existingStep.Status, child.Status), Children = AppendStepChild(existingStep, child) };
        return children;
    }

    private static IReadOnlyList<ProjectedDebugTraceItem> AppendStepChild(ProjectedDebugTraceItem stepNode, ProjectedDebugTraceItem child)
    {
        var children = stepNode.Children?.ToList() ?? [];
        if (string.IsNullOrWhiteSpace(child.CorrelationKey))
        {
            children.Add(child);
            return children;
        }

        var executionIndex = children.FindIndex(candidate => string.Equals(candidate.Kind, $"{child.Kind}-execution", StringComparison.Ordinal) && string.Equals(candidate.CorrelationKey, child.CorrelationKey, StringComparison.Ordinal));

        if (executionIndex < 0)
        {
            children.Add(CreateExecutionNode(child));
            return children;
        }

        var existingExecution = children[executionIndex];
        var executionChildren = existingExecution.Children?.ToList() ?? [];
        executionChildren.Add(child);
        children[executionIndex] = existingExecution with { Status = MergeStatus(existingExecution.Status, child.Status), Children = executionChildren };
        return children;
    }

    private static ProjectedDebugTraceItem CreateStepNode(ProjectedDebugTraceItem child) =>
        new(
            Kind: $"{child.Kind}-step",
            Name: ResolveStepNodeName(child.Kind, child.StepId!),
            Message: ResolveStepNodeMessage(child),
            Status: child.Status,
            Children: AppendStepChild(new ProjectedDebugTraceItem($"{child.Kind}-step", string.Empty, null, child.Status), child),
            StepId: child.StepId,
            SpanId: child.SpanId
        );

    private static ProjectedDebugTraceItem CreateExecutionNode(ProjectedDebugTraceItem child) =>
        new(Kind: $"{child.Kind}-execution", Name: ResolveExecutionNodeName(child), Message: ResolveExecutionNodeMessage(child), Status: child.Status, Children: [child], StepId: child.StepId, SpanId: child.SpanId, CorrelationKey: child.CorrelationKey);

    private static string ResolveGroupKind(AgentTelemetryEventKind kind) =>
        kind switch
        {
            AgentTelemetryEventKind.RunStarted or AgentTelemetryEventKind.RunCompleted or AgentTelemetryEventKind.RunFailed => "workflow",
            AgentTelemetryEventKind.NodeStarted or AgentTelemetryEventKind.NodeCompleted or AgentTelemetryEventKind.NodeFailed => "node",
            AgentTelemetryEventKind.AgentStarted or AgentTelemetryEventKind.AgentCompleted or AgentTelemetryEventKind.AgentFailed => "agent",
            AgentTelemetryEventKind.LlmCallStarted or AgentTelemetryEventKind.LlmCallCompleted => "llm",
            AgentTelemetryEventKind.ToolCallStarted or AgentTelemetryEventKind.ToolCallCompleted => "tools",
            AgentTelemetryEventKind.HumanInterventionRequested or AgentTelemetryEventKind.HumanInterventionCompleted => "human-loop",
            AgentTelemetryEventKind.ReplanTriggered => "replan",
            AgentTelemetryEventKind.ErrorRaised => "errors",
            _ => "events",
        };

    private static string ResolveGroupName(string kind) =>
        kind switch
        {
            "workflow" => "Workflow lifecycle",
            "node" => "Node lifecycle",
            "agent" => "Agent lifecycle",
            "llm" => "LLM activity",
            "tools" => "Tool activity",
            "human-loop" => "Human loop",
            "replan" => "Replanning",
            "errors" => "Errors",
            _ => "Events",
        };

    private static string ResolveGroupMessage(AgentTelemetryEnvelope envelope, string kind) =>
        kind switch
        {
            "workflow" => $"Workflow lifecycle events for run '{envelope.Correlation.RunId:D}'.",
            "node" => $"Node lifecycle events for '{envelope.Correlation.NodeExecutionId}'.",
            "agent" => $"Agent lifecycle events for run '{envelope.Correlation.NodeRunId ?? string.Empty}'.",
            "llm" => "LLM calls emitted by the structured observability runtime.",
            "tools" => "Tool calls emitted by the structured observability runtime.",
            "human-loop" => "Human-loop checkpoints emitted by the structured observability runtime.",
            "replan" => "Replanning transitions emitted by the structured observability runtime.",
            "errors" => "Structured errors emitted by the observability runtime.",
            _ => "Structured telemetry events.",
        };

    private static string ResolveStepNodeName(string kind, string stepId) =>
        kind switch
        {
            "llm" => $"LLM step {stepId}",
            "tools" => $"Tool step {stepId}",
            "human-loop" => $"Human-loop step {stepId}",
            "replan" => $"Replan step {stepId}",
            "errors" => $"Error step {stepId}",
            _ => $"Step {stepId}",
        };

    private static string ResolveStepNodeMessage(ProjectedDebugTraceItem child) => string.IsNullOrWhiteSpace(child.SpanId) ? $"Structured telemetry for step '{child.StepId}'." : $"Structured telemetry for step '{child.StepId}' (span '{child.SpanId}').";

    private static string ResolveExecutionNodeName(ProjectedDebugTraceItem child) =>
        child.Kind switch
        {
            "llm" => $"LLM call {child.SpanId ?? child.CorrelationKey}",
            "tools" => $"Tool call {GetFieldValue(child, "tool_name") ?? child.CorrelationKey}",
            "human-loop" => $"Human review {GetFieldValue(child, "question_id") ?? child.CorrelationKey}",
            "replan" => $"Replan {child.CorrelationKey}",
            "errors" => $"Error {child.CorrelationKey}",
            _ => $"Execution {child.CorrelationKey}",
        };

    private static string ResolveExecutionNodeMessage(ProjectedDebugTraceItem child) => string.IsNullOrWhiteSpace(child.SpanId) ? $"Structured lifecycle for correlation '{child.CorrelationKey}'." : $"Structured lifecycle for correlation '{child.CorrelationKey}' and span '{child.SpanId}'.";

    private static string? ResolveCorrelationKey(AgentTelemetryEnvelope envelope) =>
        envelope.Correlation.SpanId
        ?? (
            envelope.Payload switch
            {
                ToolCallStartedEvent payload => payload.ToolName,
                ToolCallCompletedEvent payload => payload.ToolName,
                HumanInterventionRequestedEvent payload => payload.QuestionId,
                HumanInterventionCompletedEvent payload => payload.QuestionId,
                LlmCallStartedEvent payload => payload.OperationKind,
                LlmCallCompletedEvent payload => payload.OperationKind,
                ReplanTriggeredEvent payload => payload.Trigger,
                ErrorRaisedEvent payload => payload.ScopeId,
                _ => null,
            }
        );

    private static string? GetFieldValue(ProjectedDebugTraceItem? item, string fieldName) => item?.Fields?.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal))?.Value;

    private static IReadOnlyList<ProjectedDebugTraceField> CreateFields(params (string Name, string Value)[] fields) => fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)).Select(field => new ProjectedDebugTraceField(field.Name, field.Value)).ToList();

    private static ProjectedDebugTraceStatus MergeStatus(ProjectedDebugTraceStatus left, ProjectedDebugTraceStatus right) =>
        (left, right) switch
        {
            (ProjectedDebugTraceStatus.Error, _) or (_, ProjectedDebugTraceStatus.Error) => ProjectedDebugTraceStatus.Error,
            (ProjectedDebugTraceStatus.Warning, _) or (_, ProjectedDebugTraceStatus.Warning) => ProjectedDebugTraceStatus.Warning,
            _ => ProjectedDebugTraceStatus.Info,
        };
}
