using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents;

/// <summary>
/// Specialised <see cref="ComposableNode"/> for teams.
/// Forces resolution to <see cref="ExecutableKind.Team"/> to avoid ambiguity
/// when a team id and a workflow id could overlap.
/// </summary>
public sealed class TeamNode : ComposableNode
{
    public new const string NodeTypeId = "team_node";

    public TeamNode(IServiceProvider services, IRunEventBus? eventBus = null)
        : base(NodeTypeId, services, eventBus)
    {
        DefineMetadata("Team", "Executes a team of agents defined in the library. The team is resolved by ID and run through the Team Engine.", "ai.agents");

        DefineInput(
            "entity_id",
            ArgumentType.Text,
            "Team ID from the agentic library",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Team",
                Order = 5,
            }
        );

        // Remove entity_kind — kind is always Team for this node.
        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    protected override ExecutableKind? ForcedKind => ExecutableKind.Team;
}
