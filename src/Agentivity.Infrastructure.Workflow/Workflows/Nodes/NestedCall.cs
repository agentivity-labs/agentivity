using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// What a step does when it calls something that runs on its own (an agent, a team, another workflow), whatever the step and
/// whatever it calls.
/// <para>
/// What is called may not finish at once: it may stop to wait (for a person, or for anything else) and say so. The step then
/// makes its own workflow wait with it, for the same thing, and keeps the run of what it called. When the workflow is taken
/// up again, the step runs a second time and calls the same run again, which goes on from where it stopped, or says it is
/// still waiting. Nothing here knows what is awaited: it is a token, handed up as it is.
/// </para>
/// </summary>
public static class NestedCall
{
    private static string RunKey(NodeConfig config) => $"__child_run_{config.Node.Id}";

    /// <summary>
    /// The run of what the step calls: a new one, or, when this step already called it and it was left waiting, that same one
    /// marked as taken up again.
    /// </summary>
    public static RunContext ChildContext(RunContext parent, IExecutable executable, string entityId, NodeConfig config)
    {
        var waitingRunId = config.State.TryGetValue(RunKey(config), out var kept) ? kept?.ToString() : null;
        var kind = executable.Definition.Kind switch
        {
            ExecutableKind.Team => RunKind.Team,
            ExecutableKind.Workflow => RunKind.Workflow,
            _ => RunKind.Agent,
        };
        return parent.CreateChild(kind, entityId, stepId: config.Node.Id, runId: waitingRunId, isResume: waitingRunId is not null);
    }

    /// <summary>
    /// Calls it. When it finishes, its result is returned. When it waits, the workflow of the step waits with it
    /// (<see cref="WorkflowWaitException"/>) and this step will be run again.
    /// </summary>
    public static async ValueTask<ExecutorResult> ExecuteAsync(IExecutable executable, IReadOnlyDictionary<string, object?> args, RunContext child, NodeConfig config, CancellationToken cancellationToken)
    {
        // Whatever the workflow was given to take this step up again (a person's answer) is for what the step called, which
        // finds it itself: it is not an input of this step.
        config.State.Remove(RunKey(config));
        config.State.Remove($"__interaction_response_{config.Node.Id}");

        // A child that failed fails this step, as an exception: the workflow goes on by the step's Error exit, or stops.
        var result = (await executable.ExecuteAsync(args, child, cancellationToken).ConfigureAwait(false)).ThrowIfFailed(executable.Definition);
        if (result.Status != ExecutableStatus.WaitingFor)
            return result;

        if (string.IsNullOrWhiteSpace(result.WaitingForToken))
            throw new InvalidOperationException($"'{executable.Definition.Name}' is waiting, and does not say for what: the workflow cannot wait with it.");

        config.State[RunKey(config)] = child.RunId;
        throw new WorkflowWaitException(result.WaitingForToken, $"Workflow waiting: '{executable.Definition.Name}', called by step '{config.Node.Id}', is waiting.");
    }
}
