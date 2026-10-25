using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Adapts <see cref="WorkflowEngine"/> towards <see cref="IExecutable"/>.
/// </summary>
public sealed class WorkflowExecutable : IExecutable
{
    private readonly string _workflowId;
    private readonly WorkflowEngine _engine;
    private readonly IInteractionRequestStore? _interactionStore;

    public WorkflowExecutable(WorkflowEntity entity, WorkflowEngine workflowEngine, IInteractionRequestStore? interactionStore = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _workflowId = entity.Id;
        _engine = workflowEngine;
        _interactionStore = interactionStore;
        Definition = entity;
    }

    public WorkflowExecutable(string workflowId, string displayName, WorkflowEngine workflowEngine, IInteractionRequestStore? interactionStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        _workflowId = workflowId;
        _engine = workflowEngine;
        _interactionStore = interactionStore;
        Definition = new MinimalWorkflowDefinition(workflowId, displayName ?? workflowId);
    }

    public IExecutableDefinition Definition { get; }

    public async ValueTask<ExecutorResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default)
    {
        var input = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        // Reuse context.RunId rather than letting the engine generate its own — otherwise the
        // workflow engine's internal run tracking (run-inspector, stream events) correlates
        // against a different id than the one the caller (and the frontend) actually knows about.
        var runId = Guid.TryParse(context.RunId, out var parsedRunId) ? parsedRunId : Guid.NewGuid();

        // Called again for a run that stopped to wait: the run is taken up where it stopped. A run that waits does not stay
        // in memory: its call returned (WaitingFor), and this is the call that follows.
        if (context.IsResume)
        {
            var (resumed, waitingFor) = await _engine.ResumeNestedAsync(runId, context, _interactionStore, ct).ConfigureAwait(false);
            return resumed is null ? ExecutorResult.WaitingFor(waitingFor) : ToResult(resumed);
        }

        return ToResult(await _engine.ExecuteAsync(runId, _workflowId, input, ct, parentContext: context).ConfigureAwait(false));
    }

    /// <summary>What the caller is told: the workflow finished, or it stopped to wait and the caller must wait with it.</summary>
    private ExecutorResult ToResult(Agentivity.Domain.Common.OperationResult<WorkflowRunResult> operationResult)
    {
        // A failure is told in the result, as every child tells it: whoever called it fails in its turn.
        if (!operationResult.IsSuccess || operationResult.Value is null)
            return ExecutorResult.Failed(operationResult.Error ?? "the workflow failed.");

        var run = operationResult.Value;
        return run.Suspended ? ExecutorResult.WaitingFor(run.WaitingForToken ?? string.Empty) : ExecutorResult.FromWorkflowState(run.State, run.ExecutedNodes, run.Definition.DeclaredOutputs);
    }

    private sealed record MinimalWorkflowDefinition(string Id, string Name) : IExecutableDefinition
    {
        public ExecutableKind Kind => ExecutableKind.Workflow;
        public string? Role => null;
        // Minimal — this wrapper doesn't carry the workflow's own tags (it's built from just an
        // id/display name for execution, not a full catalog listing), so it declares none.
        public IReadOnlyList<string> Tags => [];
        public IReadOnlyList<PortDefinition> Inputs => DefaultPorts.WorkflowInputs;
        public IReadOnlyList<PortDefinition> Outputs => DefaultPorts.WorkflowOutputs;
    }
}
