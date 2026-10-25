using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Contracts.Events.Agent;
using Agentivity.Observability.Contracts.Events.AgentControl;
using Agentivity.Observability.Contracts.Events.Llm;
using Agentivity.Observability.Contracts.Events.Node;
using Agentivity.Observability.Contracts.Events.Run;
using Agentivity.Observability.Contracts.Events.Tools;
using Agentivity.Observability.Options;
using Microsoft.Extensions.Options;

namespace Agentivity.Observability.Sinks;

public sealed class OpenTelemetryCompatibleTelemetrySink : IAgentTelemetrySink, IDisposable
{
    private readonly ConcurrentDictionary<string, Activity> _activities = new(StringComparer.Ordinal);
    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _llmCallCounter;
    private readonly Counter<long> _toolCallCounter;
    private readonly Counter<long> _replanCounter;
    private readonly Counter<long> _humanInterventionCounter;
    private readonly Counter<long> _errorCounter;
    private readonly Counter<long> _workflowRunCounter;
    private readonly Counter<long> _nodeExecutionCounter;
    private readonly Counter<long> _agentRunCounter;
    private readonly Counter<long> _tokenCounter;
    private readonly Counter<long> _agentIterationCounter;
    private readonly Counter<double> _estimatedCostCounter;
    private readonly Counter<double> _agentEstimatedCostCounter;
    private readonly Histogram<long> _workflowDurationMs;
    private readonly Histogram<long> _nodeDurationMs;
    private readonly Histogram<long> _agentDurationMs;
    private readonly Histogram<long> _llmLatencyMs;
    private readonly Histogram<long> _toolLatencyMs;
    private readonly Histogram<long> _humanInterventionLatencyMs;

    public OpenTelemetryCompatibleTelemetrySink(IOptions<OpenTelemetryCompatibleTelemetryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _activitySource = new ActivitySource(value.ActivitySourceName);
        _meter = new Meter(value.MeterName);
        _llmCallCounter = _meter.CreateCounter<long>("agentivity.llm.calls");
        _toolCallCounter = _meter.CreateCounter<long>("agentivity.tool.calls");
        _replanCounter = _meter.CreateCounter<long>("agentivity.replans");
        _humanInterventionCounter = _meter.CreateCounter<long>("agentivity.human_interventions");
        _errorCounter = _meter.CreateCounter<long>("agentivity.errors");
        _workflowRunCounter = _meter.CreateCounter<long>("agentivity.workflow.runs");
        _nodeExecutionCounter = _meter.CreateCounter<long>("agentivity.workflow.nodes");
        _agentRunCounter = _meter.CreateCounter<long>("agentivity.agent.runs");
        _tokenCounter = _meter.CreateCounter<long>("agentivity.llm.tokens");
        _agentIterationCounter = _meter.CreateCounter<long>("agentivity.agent.iterations");
        _estimatedCostCounter = _meter.CreateCounter<double>("agentivity.llm.estimated_cost");
        _agentEstimatedCostCounter = _meter.CreateCounter<double>("agentivity.agent.estimated_cost");
        _workflowDurationMs = _meter.CreateHistogram<long>("agentivity.workflow.duration_ms");
        _nodeDurationMs = _meter.CreateHistogram<long>("agentivity.workflow.node.duration_ms");
        _agentDurationMs = _meter.CreateHistogram<long>("agentivity.agent.duration_ms");
        _llmLatencyMs = _meter.CreateHistogram<long>("agentivity.llm.latency_ms");
        _toolLatencyMs = _meter.CreateHistogram<long>("agentivity.tool.latency_ms");
        _humanInterventionLatencyMs = _meter.CreateHistogram<long>("agentivity.human_intervention.latency_ms");
    }

    public ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        switch (envelope.Payload)
        {
            case RunStartedEvent payload:
                StartActivity(
                    envelope,
                    "workflow.run",
                    ActivityKind.Internal,
                    activity =>
                    {
                        activity.SetTag("workflow.id", payload.WorkflowId);
                        activity.SetTag("workflow.version", payload.WorkflowVersion);
                        activity.SetTag("environment", payload.Environment);
                    }
                );
                break;

            case RunCompletedEvent payload:
                _workflowRunCounter.Add(1, CreateTags(envelope, ("workflow.status", payload.Status)));
                _workflowDurationMs.Record(payload.DurationMs, CreateTags(envelope, ("workflow.status", payload.Status)));
                StopActivity(envelope, ActivityStatusCode.Ok, activity => activity.SetTag("workflow.status", payload.Status));
                break;

            case RunFailedEvent payload:
                StopActivity(envelope, ActivityStatusCode.Error, activity => activity.SetTag("error.message", payload.ErrorMessage));
                _errorCounter.Add(1, CreateTags(envelope, ("error.scope", "workflow")));
                break;

            case NodeStartedEvent payload:
                StartActivity(
                    envelope,
                    $"workflow.node.{payload.NodeType}",
                    ActivityKind.Internal,
                    activity =>
                    {
                        activity.SetTag("node.id", payload.NodeId);
                        activity.SetTag("node.type", payload.NodeType);
                    }
                );
                break;

            case NodeCompletedEvent payload:
                _nodeExecutionCounter.Add(1, CreateTags(envelope, ("node.type", payload.NodeType)));
                _nodeDurationMs.Record(payload.DurationMs, CreateTags(envelope, ("node.type", payload.NodeType)));
                StopActivity(envelope, ActivityStatusCode.Ok);
                break;

            case NodeFailedEvent payload:
                StopActivity(envelope, ActivityStatusCode.Error, activity => activity.SetTag("error.message", payload.ErrorMessage));
                _errorCounter.Add(1, CreateTags(envelope, ("error.scope", "node")));
                break;

            case AgentStartedEvent payload:
                StartActivity(
                    envelope,
                    $"ai.node.{payload.AgentMode}",
                    ActivityKind.Internal,
                    activity =>
                    {
                        activity.SetTag("ai.node.id", payload.AgentNodeId);
                        activity.SetTag("ai.node.mode", payload.AgentMode);
                    }
                );
                break;

            case AgentCompletedEvent payload:
                _agentRunCounter.Add(1, CreateTags(envelope, ("ai.node.mode", payload.AgentMode), ("ai.node.id", payload.AgentNodeId)));
                _agentDurationMs.Record((long)Math.Max(0, (payload.CompletedAtUtc - payload.StartedAtUtc).TotalMilliseconds), CreateTags(envelope, ("ai.node.mode", payload.AgentMode), ("ai.node.id", payload.AgentNodeId)));
                _agentIterationCounter.Add(payload.IterationCount, CreateTags(envelope, ("ai.node.mode", payload.AgentMode)));
                if (payload.TotalTokens > 0)
                {
                    _tokenCounter.Add(payload.TotalTokens, CreateTags(envelope, ("ai.node.mode", payload.AgentMode), ("ai.node.id", payload.AgentNodeId)));
                }
                if (payload.EstimatedCost > 0)
                {
                    _agentEstimatedCostCounter.Add((double)payload.EstimatedCost, CreateTags(envelope, ("ai.node.mode", payload.AgentMode), ("ai.node.id", payload.AgentNodeId)));
                }
                StopActivity(
                    envelope,
                    ActivityStatusCode.Ok,
                    activity =>
                    {
                        activity.SetTag("ai.node.iteration_count", payload.IterationCount);
                        activity.SetTag("ai.node.total_tokens", payload.TotalTokens);
                        activity.SetTag("ai.node.estimated_cost", (double)payload.EstimatedCost);
                    }
                );
                break;

            case AgentFailedEvent payload:
                StopActivity(envelope, ActivityStatusCode.Error, activity => activity.SetTag("error.message", payload.ErrorMessage));
                _errorCounter.Add(1, CreateTags(envelope, ("error.scope", "agent")));
                break;

            case LlmCallStartedEvent payload:
                StartActivity(
                    envelope,
                    $"llm.{payload.OperationKind}",
                    ActivityKind.Client,
                    activity =>
                    {
                        activity.SetTag("llm.provider", payload.Provider);
                        activity.SetTag("llm.model", payload.Model);
                        activity.SetTag("llm.operation", payload.OperationKind);
                        activity.SetTag("llm.tool_choice_mode", payload.ToolChoiceMode);
                    }
                );
                break;

            case LlmCallCompletedEvent payload:
                _llmCallCounter.Add(1, CreateTags(envelope, ("llm.provider", payload.Provider), ("llm.model", payload.Model), ("llm.operation", payload.OperationKind)));
                _llmLatencyMs.Record(payload.DurationMs, CreateTags(envelope, ("llm.operation", payload.OperationKind)));
                if (payload.TotalTokens > 0)
                {
                    _tokenCounter.Add(payload.TotalTokens, CreateTags(envelope, ("llm.model", payload.Model)));
                }
                if (payload.EstimatedCost > 0)
                {
                    _estimatedCostCounter.Add((double)payload.EstimatedCost, CreateTags(envelope, ("llm.model", payload.Model)));
                }
                if (!payload.Succeeded)
                {
                    _errorCounter.Add(1, CreateTags(envelope, ("error.scope", "llm")));
                }
                StopActivity(
                    envelope,
                    payload.Succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                    activity =>
                    {
                        activity.SetTag("llm.finish_reason", payload.FinishReason);
                        activity.SetTag("llm.total_tokens", payload.TotalTokens);
                        activity.SetTag("error.message", payload.ErrorMessage);
                    }
                );
                break;

            case ToolCallStartedEvent payload:
                StartActivity(
                    envelope,
                    $"tool.{payload.ToolName}",
                    ActivityKind.Client,
                    activity =>
                    {
                        activity.SetTag("tool.name", payload.ToolName);
                        activity.SetTag("tool.provider", payload.ToolProvider);
                        activity.SetTag("tool.arguments_hash", payload.ArgumentsHash);
                    }
                );
                break;

            case ToolCallCompletedEvent payload:
                _toolCallCounter.Add(1, CreateTags(envelope, ("tool.name", payload.ToolName), ("tool.reused_from_state", payload.ReusedFromState.ToString())));
                _toolLatencyMs.Record(payload.DurationMs, CreateTags(envelope, ("tool.name", payload.ToolName)));
                if (!payload.Succeeded)
                {
                    _errorCounter.Add(1, CreateTags(envelope, ("error.scope", "tool")));
                }
                StopActivity(
                    envelope,
                    payload.Succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                    activity =>
                    {
                        activity.SetTag("tool.reused_from_state", payload.ReusedFromState);
                        activity.SetTag("tool.result_hash", payload.ResultHash);
                        activity.SetTag("error.message", payload.ErrorMessage);
                    }
                );
                break;

            case HumanInterventionRequestedEvent payload:
                _humanInterventionCounter.Add(1, CreateTags(envelope, ("human.channel_type", payload.ChannelType)));
                StartActivity(
                    envelope,
                    "human.intervention",
                    ActivityKind.Internal,
                    activity =>
                    {
                        activity.SetTag("human.question_id", payload.QuestionId);
                        activity.SetTag("human.channel_type", payload.ChannelType);
                        activity.SetTag("human.question", payload.Question);
                    }
                );
                break;

            case HumanInterventionCompletedEvent payload:
                _humanInterventionLatencyMs.Record(payload.DurationMs, CreateTags(envelope, ("human.outcome", payload.Outcome)));
                StopActivity(
                    envelope,
                    ActivityStatusCode.Ok,
                    activity =>
                    {
                        activity.SetTag("human.outcome", payload.Outcome);
                        activity.SetTag("human.response_summary", payload.ResponseSummary);
                    }
                );
                break;

            case ReplanTriggeredEvent payload:
                _replanCounter.Add(1, CreateTags(envelope, ("replan.trigger", payload.Trigger)));
                using (var activity = StartActivityCore(envelope, "ai.node.replan", ActivityKind.Internal))
                {
                    activity?.SetTag("replan.trigger", payload.Trigger);
                    activity?.SetTag("replan.reason", payload.Reason);
                    activity?.SetTag("replan.revision_from", payload.RevisionFrom);
                    activity?.SetTag("replan.revision_to", payload.RevisionTo);
                    activity?.SetTag("replan.scope", payload.Scope);
                    activity?.SetTag("replan.preserved_task_ids", payload.PreservedTaskIds is { Count: > 0 } ? string.Join(",", payload.PreservedTaskIds) : null);
                    activity?.SetTag("replan.affected_task_ids", payload.AffectedTaskIds is { Count: > 0 } ? string.Join(",", payload.AffectedTaskIds) : null);
                    activity?.SetTag("replan.frontier_task_ids", payload.ReplanFrontierTaskIds is { Count: > 0 } ? string.Join(",", payload.ReplanFrontierTaskIds) : null);
                    activity?.SetTag("replan.graph_revision_count", payload.GraphRevisionCount);
                    activity?.SetTag("replan.graph_task_count", payload.GraphTaskCount);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                break;

            case ErrorRaisedEvent payload:
                _errorCounter.Add(1, CreateTags(envelope, ("error.scope", payload.ScopeType), ("error.terminal", payload.IsTerminal.ToString())));
                using (var activity = StartActivityCore(envelope, "ai.node.error", ActivityKind.Internal))
                {
                    activity?.SetTag("error.scope", payload.ScopeType);
                    activity?.SetTag("error.scope_id", payload.ScopeId);
                    activity?.SetTag("error.code", payload.ErrorCode);
                    activity?.SetTag("error.terminal", payload.IsTerminal);
                    activity?.SetStatus(ActivityStatusCode.Error, payload.ErrorMessage);
                }
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void StartActivity(AgentTelemetryEnvelope envelope, string name, ActivityKind kind, Action<Activity>? configure = null)
    {
        var activity = StartActivityCore(envelope, name, kind);
        if (activity is null)
        {
            return;
        }

        configure?.Invoke(activity);
        _activities[BuildKey(envelope)] = activity;
    }

    private Activity? StartActivityCore(AgentTelemetryEnvelope envelope, string name, ActivityKind kind)
    {
        var parentActivity = TryGetParentActivity(envelope);
        var parentContext = parentActivity?.Context ?? default;
        var activity = parentActivity is not null ? _activitySource.StartActivity(name, kind, parentContext) : _activitySource.StartActivity(name, kind);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("agentivity.run_id", envelope.Correlation.RunId);
        activity.SetTag("agentivity.entity_id", envelope.Correlation.EntityId);
        activity.SetTag("agentivity.node_run_id", envelope.Correlation.NodeRunId);
        activity.SetTag("agentivity.node_execution_id", envelope.Correlation.NodeExecutionId);
        activity.SetTag("agentivity.step_id", envelope.Correlation.StepId);
        activity.SetTag("workflow.run.id", envelope.Correlation.RunId);
        activity.SetTag("workflow.id", envelope.Correlation.EntityId);
        activity.SetTag("ai.node.run.id", envelope.Correlation.NodeRunId);
        activity.SetTag("node.execution.id", envelope.Correlation.NodeExecutionId);
        activity.SetTag("step.id", envelope.Correlation.StepId);
        activity.SetTag("agentivity.original_trace_id", envelope.Correlation.TraceId);
        activity.SetTag("agentivity.original_span_id", envelope.Correlation.SpanId);
        activity.SetTag("agentivity.original_parent_span_id", envelope.Correlation.ParentSpanId);
        return activity;
    }

    private void StopActivity(AgentTelemetryEnvelope envelope, ActivityStatusCode statusCode, Action<Activity>? configure = null)
    {
        if (!_activities.TryRemove(BuildKey(envelope), out var activity))
        {
            return;
        }

        configure?.Invoke(activity);
        activity.SetStatus(statusCode);
        activity.Dispose();
    }

    private Activity? TryGetParentActivity(AgentTelemetryEnvelope envelope)
    {
        var parentSpanId = envelope.Correlation.ParentSpanId;
        if (string.IsNullOrWhiteSpace(parentSpanId))
        {
            return null;
        }

        _activities.TryGetValue($"{envelope.Correlation.RunId:N}:{parentSpanId}", out var parent);
        return parent;
    }

    private static string BuildKey(AgentTelemetryEnvelope envelope) => $"{envelope.Correlation.RunId:N}:{envelope.Correlation.SpanId}";

    private static TagList CreateTags(AgentTelemetryEnvelope envelope, params (string Key, string? Value)[] tags)
    {
        var tagList = new TagList
        {
            { "workflow.id", envelope.Correlation.EntityId },
            { "ai.node.run.id", envelope.Correlation.NodeRunId },
            { "node.execution.id", envelope.Correlation.NodeExecutionId },
            { "step.id", envelope.Correlation.StepId },
        };

        foreach (var (key, value) in tags)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                tagList.Add(key, value);
            }
        }

        return tagList;
    }

    public void Dispose()
    {
        foreach (var activity in _activities.Values)
        {
            activity.Dispose();
        }

        _activities.Clear();
        _activitySource.Dispose();
        _meter.Dispose();
    }
}
