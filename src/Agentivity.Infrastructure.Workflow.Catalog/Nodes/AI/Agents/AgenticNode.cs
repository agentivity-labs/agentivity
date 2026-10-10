using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents;

/// <summary>
/// Workflow node that executes a catalogued entity (agent or team) selected by ID.
/// Resolves the entity via <see cref="IExecutableResolver"/> and delegates execution
/// entirely to the resolved <see cref="IExecutable"/> — no engine-specific logic here.
/// <para>
/// The underlying engine (agent, team, workflow) drives its own proxies and pipeline.
/// <c>AgenticNode</c> only reads <c>agent_id</c> and <c>input</c>,
/// builds a child <see cref="RunContext"/>, and forwards to the executable.
/// </para>
/// </summary>
public sealed class AgenticNode : BaseNode
{
    public new const string NodeType = "agentic_node";

    private readonly IServiceProvider _services;
    private readonly IRunEventBus? _eventBus;

    // Resolved lazily — avoids circular DI: AgenticNode → IExecutableResolver → ... → AgenticNode
    private IExecutableResolver Resolver => _services.GetRequiredService<IExecutableResolver>();

    public AgenticNode(IServiceProvider services, IRunEventBus? eventBus = null)
        : base(NodeType)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _eventBus = eventBus;

        DefineMetadata("Library Agent", "Executes an agent or team from the agentic library. Select an entity by ID — the engine and configuration are resolved at runtime.", "ai.agents");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1, color: "#FF607D8B");
        DefineOutputPort("out", "Out", "Entity response");
        DefineOutputPort("error", "Error", "Execution error if any");

        DefineInput(
            "agent_id",
            ArgumentType.Text,
            "Agent or Team ID — select an entity from the library",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Agent",
                Order = 5,
            }
        );

        DefineInput(
            "input",
            ArgumentType.Text,
            "User input / goal sent to the entity",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Agent",
                Order = 10,
                Multiline = true,
                Rows = 6,
            }
        );

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        var entityId = config.Resolve("agent_id")?.ToString();
        if (string.IsNullOrWhiteSpace(entityId))
            throw new InvalidOperationException($"{nameof(AgenticNode)}: 'agent_id' is required but was not configured.");

        var input = config.Resolve("input")?.ToString() ?? string.Empty;

        // Resolve executable — agent, team, or workflow.
        IExecutable executable = await Resolver.ResolveAsync(entityId, cancellationToken).ConfigureAwait(false);

        // Build child RunContext from workflow correlation.
        var parentCtx = services.CorrelationContext is not null ? RunContext.FromCorrelationContext(services.CorrelationContext, RunKind.Workflow) : RunContext.StartNew(RunKind.Workflow, config.Definition.Id);

        var childCtx = parentCtx.CreateChild(ToRunKind(executable.Definition.Kind), entityId, stepId: config.Node.Id);

        _eventBus?.Publish(
            new RunEvent(
                RunId: parentCtx.RunId,
                DefinitionId: parentCtx.EntityId,
                RunKind: parentCtx.Kind,
                Kind: RunEventKind.node_started,
                EventName: "AgenticNodeStarted",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Payload: new
                {
                    nodeId = config.Node.Id,
                    entityId,
                    displayName = executable.Definition.Name,
                },
                ExecutionId: parentCtx.Execution.ExecutionId
            )
        );

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = input };

        ExecutorResult result;
        try
        {
            result = await executable.ExecuteAsync(args, childCtx, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _eventBus?.Publish(
                new RunEvent(
                    RunId: parentCtx.RunId,
                    DefinitionId: parentCtx.EntityId,
                    RunKind: parentCtx.Kind,
                    Kind: RunEventKind.node_failed,
                    EventName: "AgenticNodeFailed",
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Payload: new { nodeId = config.Node.Id, error = ex.Message },
                    ExecutionId: parentCtx.Execution.ExecutionId
                )
            );
            throw;
        }

        _eventBus?.Publish(
            new RunEvent(
                RunId: parentCtx.RunId,
                DefinitionId: parentCtx.EntityId,
                RunKind: parentCtx.Kind,
                Kind: RunEventKind.node_completed,
                EventName: "AgenticNodeCompleted",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Payload: new { nodeId = config.Node.Id, content = result.Content },
                ExecutionId: parentCtx.Execution.ExecutionId
            )
        );

        // Map ExecutorResult → NodeResult.
        var nodeResult = NodeResult.Create();
        if (!string.IsNullOrWhiteSpace(result.Content))
            nodeResult.AddText("response", result.Content);
        if (result.FinishReason is not null)
            nodeResult.AddText("finish_reason", result.FinishReason);
        foreach (var (key, value) in result.Outputs)
        {
            switch (value)
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

    private static RunKind ToRunKind(ExecutableKind kind) =>
        kind switch
        {
            ExecutableKind.Team => RunKind.Team,
            ExecutableKind.Workflow => RunKind.Workflow,
            _ => RunKind.Agent,
        };
}
