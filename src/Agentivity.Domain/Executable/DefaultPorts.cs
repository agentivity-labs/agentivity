namespace Agentivity.Domain.Executable;

/// <summary>
/// Ports par défaut partagés entre <c>AgentEntity</c>, <c>TeamEntity</c> et
/// <c>WorkflowEntity</c>. Chaque entité peut surcharger ces défauts si son schéma
/// est plus précis (ex : workflow avec paramètres déclarés explicitement).
/// </summary>
public static class DefaultPorts
{
    public static IReadOnlyList<PortDefinition> AgentInputs { get; } = [new PortDefinition("input", "text", "Prompt or goal sent to the agent")];

    public static IReadOnlyList<PortDefinition> AgentOutputs { get; } = [new PortDefinition("output", "text", "Agent response")];

    public static IReadOnlyList<PortDefinition> TeamInputs { get; } = [new PortDefinition("input", "text", "Goal sent to the team")];

    public static IReadOnlyList<PortDefinition> TeamOutputs { get; } = [new PortDefinition("output", "text", "Team result")];

    public static IReadOnlyList<PortDefinition> WorkflowInputs { get; } = [new PortDefinition("input", "text", "Workflow input")];

    public static IReadOnlyList<PortDefinition> WorkflowOutputs { get; } = [new PortDefinition("output", "text", "Workflow output")];
}
