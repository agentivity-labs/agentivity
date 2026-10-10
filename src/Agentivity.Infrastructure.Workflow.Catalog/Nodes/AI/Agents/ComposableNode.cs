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
/// Nœud workflow générique capable d'exécuter un agent, une team ou un workflow imbriqué.
/// Résout l'entité via <see cref="IExecutableResolver"/> — aucun couplage sur le type de moteur.
/// <para>
/// Inputs :
/// <list type="bullet">
///   <item><c>entity_id</c>   — ID de l'entité (requis)</item>
///   <item><c>entity_kind</c> — "agent" | "team" | "workflow" (optionnel, auto-détecté)</item>
///   <item><c>input</c>       — goal / prompt (requis)</item>
/// </list>
/// </para>
/// </summary>
public class ComposableNode : BaseNode
{
    public const string NodeTypeId = "composable_node";

    private readonly IServiceProvider _services;
    private readonly IRunEventBus? _eventBus;

    // Resolved lazily to avoid a circular DI dependency:
    // ComposableNode -> IExecutableResolver -> DefaultExecutableResolver -> WorkflowEngine -> IEnumerable<Node> -> ComposableNode
    private IExecutableResolver Resolver => _services.GetRequiredService<IExecutableResolver>();

    public ComposableNode(IServiceProvider services, IRunEventBus? eventBus = null)
        : this(NodeTypeId, services, eventBus) { }

    protected ComposableNode(string nodeTypeId, IServiceProvider services, IRunEventBus? eventBus = null)
        : base(nodeTypeId)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _eventBus = eventBus;

        DefineMetadata("Composable", "Executes an agent, a team, or a workflow from the library. Select an entity by ID — the engine is resolved automatically at runtime.", "ai.agents");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1, color: "#FF607D8B");
        DefineOutputPort("out", "Out", "Composable response");
        DefineOutputPort("error", "Error", "Execution error if any");

        DefineInput(
            "entity_id",
            ArgumentType.Text,
            "Entity ID — agent, team, or workflow from the library",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Composable",
                Order = 5,
            }
        );

        DefineInput(
            "entity_kind",
            ArgumentType.Text,
            "Entity kind: agent | team | workflow (optional — auto-detected when omitted)",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Composable",
                Order = 10,
            }
        );

        DefineInput(
            "input",
            ArgumentType.Text,
            "Goal / prompt sent to the composable",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Composable",
                Order = 15,
                Multiline = true,
                Rows = 6,
            }
        );

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        var entityId = config.Resolve("entity_id")?.ToString();
        if (string.IsNullOrWhiteSpace(entityId))
            throw new InvalidOperationException($"{GetType().Name}: 'entity_id' is required but was not configured.");

        var input = config.Resolve("input")?.ToString() ?? string.Empty;

        // Resolve executable.
        var kindHint = ForcedKind ?? TryParseKind(config.Resolve("entity_kind")?.ToString());
        IExecutable executable = kindHint.HasValue ? Resolver.Resolve(kindHint.Value, entityId) : await Resolver.ResolveAsync(entityId, cancellationToken).ConfigureAwait(false);

        // Build RunContext from workflow correlation or start a new one.
        var parentCtx = services.CorrelationContext is not null ? RunContext.FromCorrelationContext(services.CorrelationContext, RunKind.Workflow) : RunContext.StartNew(RunKind.Workflow, config.Definition.Id);

        var childCtx = parentCtx.CreateChild(ToRunKind(executable.Definition.Kind), entityId, stepId: config.Node.Id);

        // Emit StepStarted on unified bus.
        _eventBus?.Publish(
            new RunEvent(
                RunId: parentCtx.RunId,
                DefinitionId: parentCtx.EntityId,
                RunKind: parentCtx.Kind,
                Kind: RunEventKind.node_started,
                EventName: "ComposableNodeStarted",
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
                    EventName: "ComposableNodeFailed",
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
                EventName: "ComposableNodeCompleted",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Payload: new { nodeId = config.Node.Id, content = result.Content },
                ExecutionId: parentCtx.Execution.ExecutionId
            )
        );

        var nodeResult = NodeResult.Create();
        if (!string.IsNullOrWhiteSpace(result.Content))
            nodeResult.AddText("output", result.Content);
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

    // ── Extension points ─────────────────────────────────────────────────────

    /// <summary>
    /// Override in subclasses to force a specific kind instead of relying on the
    /// <c>entity_kind</c> node parameter.
    /// </summary>
    protected virtual ExecutableKind? ForcedKind => null;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExecutableKind? TryParseKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Enum.TryParse<ExecutableKind>(raw, ignoreCase: true, out var k) ? k : null;
    }

    private static RunKind ToRunKind(ExecutableKind kind) =>
        kind switch
        {
            ExecutableKind.Team => RunKind.Team,
            ExecutableKind.Workflow => RunKind.Workflow,
            _ => RunKind.Agent,
        };
}
