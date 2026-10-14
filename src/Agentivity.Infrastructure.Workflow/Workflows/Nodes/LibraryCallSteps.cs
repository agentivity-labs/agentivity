using Agentivity.Domain.Executable;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// The steps a workflow calls something of the library with: one per kind of thing it calls, an agent, a team or a workflow.
/// Each one names what it calls the same way, by its id in <see cref="EntityInput"/>, and calls it through
/// <see cref="NestedCall"/>. Whoever needs to know which step calls what (the step itself, what follows the references of a
/// workflow, the Templates) reads it here.
/// </summary>
public static class LibraryCallSteps
{
    public const string Agent = "agentic_node";
    public const string Team = "team_node";
    public const string Workflow = "workflow_node";

    /// <summary>Where each of these steps keeps the id of what it calls.</summary>
    public const string EntityInput = "entity_id";

    /// <summary>What a step of this type calls; null when the step does not call the library.</summary>
    public static ExecutableKind? KindOf(string? nodeType) =>
        nodeType?.ToLowerInvariant() switch
        {
            Agent => ExecutableKind.Agent,
            Team => ExecutableKind.Team,
            Workflow => ExecutableKind.Workflow,
            _ => null,
        };
}
