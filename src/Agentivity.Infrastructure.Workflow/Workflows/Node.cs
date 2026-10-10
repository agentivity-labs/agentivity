using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Execution;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows;

public abstract class Node : IExecutionUnit<NodeConfig, NodeResult>
{
    protected Node(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Node name is required", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    public abstract ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default);

    // ── IExecutionUnit<NodeConfig, NodeResult> ────────────────────────────────
    // Allows any orchestrator holding IExecutionUnit to dispatch to a node
    // using only the common IRunService contract.
    ValueTask<NodeResult> IExecutionUnit<NodeConfig, NodeResult>.ExecuteAsync(NodeConfig config, IRunService services, CancellationToken cancellationToken) =>
        services is WorkflowRunServices wrs ? ExecuteAsync(config, wrs, cancellationToken) : throw new InvalidOperationException($"Node '{Name}' requires a {nameof(WorkflowRunServices)} but received {services.GetType().Name}.");
}

public sealed class NodeContext
{
    private DebugTraceAccumulator? _traceAccumulator;
    private readonly IRepository<RunContext, RunRecord>? _registry;

    public NodeContext(WorkflowEntity definition, WorkflowNodeDefinition node, IDictionary<string, object?> state)
        : this(definition, node, state, Guid.Empty, registry: null, telemetryEmitter: null, workflowCorrelationContext: null, logger: null) { }

    public NodeContext(WorkflowEntity definition, WorkflowNodeDefinition node, IDictionary<string, object?> state, ILogger? logger)
        : this(definition, node, state, Guid.Empty, registry: null, telemetryEmitter: null, workflowCorrelationContext: null, logger: logger) { }

    public NodeContext(WorkflowEntity definition, WorkflowNodeDefinition node, IDictionary<string, object?> state, Guid runId, IAgentTelemetryEmitter? telemetryEmitter = null, ExecutionCorrelationContext? workflowCorrelationContext = null, ILogger? logger = null)
        : this(definition, node, state, runId, registry: null, telemetryEmitter: telemetryEmitter, workflowCorrelationContext: workflowCorrelationContext, logger: logger) { }

    public NodeContext(WorkflowEntity definition, WorkflowNodeDefinition node, IDictionary<string, object?> state, Guid runId, IRepository<RunContext, RunRecord>? registry, IAgentTelemetryEmitter? telemetryEmitter = null, ExecutionCorrelationContext? workflowCorrelationContext = null, ILogger? logger = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        State = state ?? throw new ArgumentNullException(nameof(state));
        RunId = runId;
        _registry = registry;
        TelemetryEmitter = telemetryEmitter;
        WorkflowCorrelationContext = workflowCorrelationContext;
        Logger = logger;
    }

    public WorkflowEntity Definition { get; }

    public WorkflowNodeDefinition Node { get; }

    public IDictionary<string, object?> State { get; }

    public Guid RunId { get; }

    public IAgentTelemetryEmitter? TelemetryEmitter { get; }

    public ExecutionCorrelationContext? WorkflowCorrelationContext { get; }

    internal ILogger? Logger { get; }

    /// <summary>
    /// Pure configuration snapshot — graph, node definition, state.
    /// No runtime services. Cached after first access so that <see cref="ResolveAll"/>
    /// populates the same instance that <see cref="ExecuteAsync"/> receives.
    /// </summary>
    public NodeConfig Config => _config ??= new(Definition, Node, State);

    private NodeConfig? _config;

    /// <summary>
    /// Runtime services bundle — delegates for checkpoint, status, trace and telemetry.
    /// No graph/config data. Pass this to coordinators that need to interact with the workflow engine.
    /// </summary>
    public WorkflowRunServices Services => new(runId: RunId, checkpointAsync: ct => CheckpointAsync(ct), publishStatus: msg => PublishStatusMessage(msg), updateTrace: updater => UpdateTrace(updater), telemetryEmitter: TelemetryEmitter, correlationContext: WorkflowCorrelationContext, logger: Logger);

    internal IReadOnlyDictionary<string, object?>? ResolvedInputs { get; private set; }

    // ── Sync bridging helper over the async IRepository surface ─────────────
    // FindAsync's cache-hit path is synchronous under the hood (ConcurrentDictionary
    // lookup, no I/O) — same reasoning applied elsewhere in this codebase (WorkflowEngine.cs).
    private RunContext? GetRegistryRun(string runId) => _registry?.FindAsync(runId).AsTask().GetAwaiter().GetResult();

    public object? Resolve(string parameterName)
    {
        // Parameters is now JsonElement - use GetParameter helper
        var raw = Node.GetParameter(parameterName);
        if (raw is null)
        {
            return null;
        }

        return ArgumentValueResolver.Resolve(raw.Value, State);
    }

    public void ResolveAll()
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Parameters is now JsonElement - enumerate properties
        if (Node.Parameters.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in Node.Parameters.EnumerateObject())
            {
                var value = ArgumentValueResolver.Resolve(property.Value, State);
                if (value is not null)
                {
                    resolved[property.Name] = value;
                }
            }
        }

        ResolvedInputs = resolved;
        // Populate the cached NodeConfig so that callers holding context.Config
        // see the resolved inputs immediately after context.ResolveAll().
        Config.SetResolvedInputs(resolved);
    }

    /// <summary>
    /// Tries to get the run control for pause/resume/cancel operations.
    /// </summary>
    public bool TryGetRunControl(out RunControl? control)
    {
        control = null;
        if (RunId == Guid.Empty)
        {
            return false;
        }

        control = GetRegistryRun(RunId.ToString("D"))?.Control;
        return control is not null;
    }

    /// <summary>
    /// Checks if the workflow should pause and waits if paused.
    /// Call this at long-running operations within node execution.
    /// </summary>
    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetRunControl(out var control) && control is not null)
        {
            await control.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes a status message visible in the workflow UI (graph display).
    /// Also emits an ILogger Information event for internal diagnostics.
    /// </summary>
    public void PublishStatusMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var logRunId = RunId;
        if (logRunId == Guid.Empty && !Guid.TryParse(Definition.Id, out logRunId))
        {
            Logger?.LogInformation("Workflow node {NodeId} ({NodeType}) published status message in workflow {WorkflowId}: {StatusMessage}", Node.Id, Node.NodeType, Definition.Id, message);
            return;
        }

        GetRegistryRun(logRunId.ToString("D"))?.AppendStepLog(Node.Id, message);
        Logger?.LogInformation("Workflow node {NodeId} ({NodeType}) published status message for run {WorkflowRunId} in workflow {WorkflowId}: {StatusMessage}", Node.Id, Node.NodeType, logRunId, Definition.Id, message);
    }

    public void LogDebug(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Logger?.LogDebug("Workflow node {NodeId} ({NodeType}) [debug] run {WorkflowRunId}: {NodeLogMessage}", Node.Id, Node.NodeType, RunId, message);
    }

    public void SetTrace(DebugTraceItem? trace, bool notify = true)
    {
        if (!TryGetTraceRunId(out var traceRunId))
        {
            return;
        }

        var accumulator = GetOrCreateTraceAccumulator(traceRunId);
        accumulator.ReplaceRoot(trace);
        // Note: debug trace persistence not supported in IRunRepository (no-op)
    }

    public DebugTraceItem? GetTrace()
    {
        if (!TryGetTraceRunId(out var traceRunId))
        {
            return null;
        }

        return GetOrCreateTraceAccumulator(traceRunId).Build();
    }

    public void UpdateTrace(Action<DebugTraceAccumulator> update, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!TryGetTraceRunId(out var traceRunId))
        {
            return;
        }

        var accumulator = GetOrCreateTraceAccumulator(traceRunId);
        update(accumulator);
        GetRegistryRun(traceRunId.ToString("D"))?.SetStepTrace(Node.Id, accumulator.Build());
    }

    public void AppendTrace(DebugTraceItem trace, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(trace);

        UpdateTrace(accumulator => accumulator.Append(trace), notify);
    }

    public DebugTraceScope PushTrace(DebugTraceItem trace, bool notify = true, Predicate<DebugTraceNode>? match = null)
    {
        ArgumentNullException.ThrowIfNull(trace);

        if (!TryGetTraceRunId(out var traceRunId))
        {
            return default;
        }

        var accumulator = GetOrCreateTraceAccumulator(traceRunId);
        accumulator.Push(trace, match);
        // Note: debug trace persistence not supported in IRunRepository (no-op)
        return new DebugTraceScope(() => PopTrace(notify));
    }

    public void PopTrace(bool notify = true)
    {
        if (!TryGetTraceRunId(out var traceRunId))
        {
            return;
        }

        var accumulator = GetOrCreateTraceAccumulator(traceRunId);
        if (!accumulator.Pop())
        {
            return;
        }

        // Note: debug trace persistence not supported in IRunRepository (no-op)
    }

    private bool TryGetTraceRunId(out Guid traceRunId)
    {
        traceRunId = RunId;
        if (traceRunId != Guid.Empty)
        {
            return true;
        }

        return Guid.TryParse(Definition.Id, out traceRunId);
    }

    private DebugTraceAccumulator GetOrCreateTraceAccumulator(Guid traceRunId)
    {
        _traceAccumulator ??= new DebugTraceAccumulator(
            null /* trace not stored in IRunRepository */
        );
        return _traceAccumulator;
    }
}
