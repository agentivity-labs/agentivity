using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents;

/// <summary>Calls an agent of the library and gives its answer.</summary>
public sealed class AgentCallNode(IServiceProvider services, IRunEventBus? eventBus = null)
    : LibraryCallNode(LibraryCallSteps.Agent, ExecutableKind.Agent, "Agent", "Calls an agent of your library with a message, and gives its answer.", "To reuse an agent saved in your library: send it a message and get its answer back. Not for an agent built only for this workflow: use AI Agent instead.", "ai.agents", "aillm", services, eventBus);

/// <summary>Calls a team of the library and gives its answer.</summary>
public sealed class TeamNode(IServiceProvider services, IRunEventBus? eventBus = null)
    : LibraryCallNode(LibraryCallSteps.Team, ExecutableKind.Team, "Team", "Calls a team of your library with a message, and gives its answer.", "To hand a task to a team of agents saved in your library and get its answer back. For a single agent, use Agent instead.", "ai.agents", "aillm", services, eventBus);

/// <summary>Calls another workflow of the library and gives what it produced.</summary>
public sealed class WorkflowCallNode(IServiceProvider services, IRunEventBus? eventBus = null)
    : LibraryCallNode(LibraryCallSteps.Workflow, ExecutableKind.Workflow, "Workflow", "Calls another workflow of your library with a message, and gives what it produced.", "To run another workflow of your library as one step of this one, and use what it produced. If that workflow waits for a person, this one waits with it.", "core.workflow", "workflow", services, eventBus);

/// <summary>
/// What the three steps that call something of the library have in common (see <see cref="LibraryCallSteps"/>): the id of what
/// they call, the message sent to it, and the call itself, through <see cref="NestedCall"/>. Each step calls only its own kind.
/// </summary>
public abstract class LibraryCallNode : BaseNode
{
    private readonly IServiceProvider _services;
    private readonly IRunEventBus? _eventBus;
    private readonly ExecutableKind _kind;

    // Resolved lazily to avoid a circular DI dependency:
    // the step -> IExecutableResolver -> DefaultExecutableResolver -> WorkflowEngine -> IEnumerable<Node> -> the step
    private IExecutableResolver Resolver => _services.GetRequiredService<IExecutableResolver>();

    protected LibraryCallNode(string nodeTypeId, ExecutableKind kind, string displayName, string description, string whenToUse, string category, string icon, IServiceProvider services, IRunEventBus? eventBus)
        : base(nodeTypeId)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _eventBus = eventBus;
        _kind = kind;
        var what = kind.ToString().ToLowerInvariant();

        DefineMetadata(displayName, description, category, whenToUse: whenToUse);
        DefineEffects(NodeEffects.Costs | NodeEffects.Delegates);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1, color: "#FF607D8B");
        DefineOutputPort("out", "Out", $"The answer of the {what}");
        DefineOutputPort("error", "Error", "Execution error if any");
        DefineOutput("output", ArgumentType.Text, $"The final answer of the {what}.");
        DefineDynamicOutputs($"Any named output that the {what} itself declares is added next to this one, under its own name.");

        DefineInput(
            LibraryCallSteps.EntityInput,
            ArgumentType.Text,
            $"The {what} of your library to call",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = displayName,
                Order = 5,
            },
            required: true
        );

        DefineInput(
            "input",
            ArgumentType.Text,
            $"Message sent to the {what}",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = displayName,
                Order = 15,
                Multiline = true,
                Rows = 6,
            },
            required: true
        );

        DefineIcon(NodeIcon.Embedded(icon));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        var entityId = config.Resolve(LibraryCallSteps.EntityInput)?.ToString();
        var what = _kind.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(entityId))
            throw new InvalidOperationException($"No {what} is chosen for step '{config.Node.Id}'.");

        var input = config.Resolve("input")?.ToString() ?? string.Empty;

        IExecutable executable = await Resolver.ResolveAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (executable.Definition.Kind != _kind)
            throw new InvalidOperationException($"'{executable.Definition.Name}' is a {executable.Definition.Kind.ToString().ToLowerInvariant()}, and step '{config.Node.Id}' calls a {what}.");

        // Build RunContext from workflow correlation or start a new one.
        var parentCtx = services.CorrelationContext is not null ? RunContext.FromCorrelationContext(services.CorrelationContext, RunKind.Workflow) : RunContext.StartNew(RunKind.Workflow, config.Definition.Id);

        var childCtx = NestedCall.ChildContext(parentCtx, executable, entityId, config);

        Publish(parentCtx, RunEventKind.node_started, "LibraryCallStarted", new { nodeId = config.Node.Id, entityId, displayName = executable.Definition.Name });

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = input };

        ExecutorResult result;
        try
        {
            // It finishes, or it waits and this workflow waits with it (NestedCall): that is not a failure of the step.
            result = await NestedCall.ExecuteAsync(executable, args, childCtx, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not WorkflowWaitException)
        {
            Publish(parentCtx, RunEventKind.node_failed, "LibraryCallFailed", new { nodeId = config.Node.Id, error = ex.Message });
            throw;
        }

        Publish(parentCtx, RunEventKind.node_completed, "LibraryCallCompleted", new { nodeId = config.Node.Id, content = result.Content });

        var nodeResult = NodeResult.Create();
        if (!string.IsNullOrWhiteSpace(result.Content))
            nodeResult.AddText("output", result.Content);
        foreach (var (key, value) in result.Outputs)
        {
            switch (NamedOutputs.Normalize(value))
            {
                case string s:
                    nodeResult.AddText(key, s);
                    break;
                case long l:
                    nodeResult.AddNumber(key, l);
                    break;
                case int i:
                    nodeResult.AddNumber(key, (long)i);
                    break;
                case double d:
                    nodeResult.AddNumber(key, d);
                    break;
                case bool b:
                    nodeResult.AddBoolean(key, b);
                    break;
                case IReadOnlyList<string> list:
                    nodeResult.AddTextList(key, list);
                    break;
            }
        }
        return nodeResult;
    }

    private void Publish(RunContext parent, RunEventKind kind, string eventName, object payload) =>
        _eventBus?.Publish(
            new RunEvent(
                RunId: parent.RunId,
                DefinitionId: parent.EntityId,
                RunKind: parent.Kind,
                Kind: kind,
                EventName: eventName,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Payload: payload,
                ExecutionId: parent.Execution.ExecutionId
            )
        );
}
