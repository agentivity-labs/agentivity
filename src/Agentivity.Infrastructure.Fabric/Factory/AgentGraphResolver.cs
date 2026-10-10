using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Résout les policies, guardrails et session d'un <see cref="AgentEntity"/> directement
/// depuis son graph — même logique que <see cref="AgentPolicyResourceResolver"/>,
/// <see cref="AgentGuardrailResolver"/> et la session en workflow, mais sans <see cref="Agentivity.Infrastructure.Workflows.NodeContext"/>.
/// </summary>
public static class AgentGraphResolver
{
    private const string LlmModelPortName = "llm_model";
    private const string PoliciesPortName = "policies";
    private const string GuardrailsPortName = "guardrails";
    private const string SessionPortName = "session";
    private const string ChannelPortName = "channel";

    /// <summary>
    /// Résout toutes les policy layers connectées à <paramref name="aiNodeId"/> via le port <c>policies</c>.
    /// Retourne une liste vide si aucune policy source n'est connectée ou connue.
    /// </summary>
    public static IReadOnlyList<AgentPolicyLayer> ResolvePolicyLayers(AgentEntity agent, string aiNodeId)
    {
        var connections = agent.Graph.Connections.Where(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, PoliciesPortName, StringComparison.OrdinalIgnoreCase));

        var layers = new List<AgentPolicyLayer>();
        foreach (var conn in connections)
        {
            var node = agent.Graph.FindNode(conn.From);
            if (node is null)
                continue;

            var policySource = NodeInstanceRegistry.GetAs<IAgentPolicySource>(node.NodeType);
            if (policySource is null)
                continue;

            var resourceType = ResourceNodeRegistry.ResolveResourceType(node.NodeType) ?? ResourcePortCategory.AIAgentPolicy;
            var binding = new ResourceBinding(conn.From, conn.FromPort, node, resourceType);
            var layer = policySource.BuildPolicyLayer(binding);
            if (layer is not null)
                layers.Add(layer);
        }

        return layers;
    }

    /// <summary>
    /// Résout toutes les guardrail definitions connectées à <paramref name="aiNodeId"/> via le port <c>guardrails</c>.
    /// Retourne une liste vide si aucune guardrail source n'est connectée ou connue.
    /// </summary>
    public static IReadOnlyList<AgentGuardrailDefinition> ResolveGuardrails(AgentEntity agent, string aiNodeId)
    {
        var connections = agent.Graph.Connections.Where(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, GuardrailsPortName, StringComparison.OrdinalIgnoreCase));

        var guardrails = new List<AgentGuardrailDefinition>();
        foreach (var conn in connections)
        {
            var node = agent.Graph.FindNode(conn.From);
            if (node is null)
                continue;

            var guardrailSource = NodeInstanceRegistry.GetAs<IAgentGuardrailSource>(node.NodeType);
            if (guardrailSource is null)
                continue;

            var resourceType = ResourceNodeRegistry.ResolveResourceType(node.NodeType) ?? ResourcePortCategory.AIAgentGuardrail;
            var binding = new ResourceBinding(conn.From, conn.FromPort, node, resourceType);
            var guardrail = guardrailSource.BuildGuardrail(binding);
            if (guardrail is not null)
                guardrails.Add(guardrail);
        }

        return guardrails.OrderBy(g => g.Priority).ThenBy(g => g.GuardrailId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Résout le nœud session connecté à <paramref name="aiNodeId"/> via le port <c>session</c>.
    /// Retourne null si aucun nœud session n'est connecté ou reconnu.
    /// </summary>
    public static (IAgentSession Session, ResourceBinding Binding)? ResolveSession(AgentEntity agent, string aiNodeId)
    {
        var conn = agent.Graph.Connections.FirstOrDefault(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, SessionPortName, StringComparison.OrdinalIgnoreCase));

        if (conn is null)
            return null;

        var node = agent.Graph.FindNode(conn.From);
        if (node is null)
            return null;

        var sessionSource = NodeInstanceRegistry.GetAs<IAgentSession>(node.NodeType);
        if (sessionSource is null)
            return null;

        var resourceType = ResourceNodeRegistry.ResolveResourceType(node.NodeType) ?? ResourcePortCategory.AIAgentSession;
        var binding = new ResourceBinding(conn.From, conn.FromPort, node, resourceType);
        return (sessionSource, binding);
    }

    /// <summary>
    /// Résout le nœud de canal d'interaction pour <paramref name="aiNodeId"/>.
    /// Priorité : connexion explicite sur le port <c>channel</c> du nœud ai.
    /// Fallback : premier nœud <see cref="IHumanInteractionChannel"/> dans le graph (agents team
    /// qui exposent le nœud canal sans connexion de port explicite).
    /// Retourne null si aucun nœud canal n'est présent dans le graph.
    /// </summary>
    public static ChannelBinding? TryResolveInteractionChannel(AgentEntity agent, string aiNodeId)
    {
        var conn = agent.Graph.Connections.FirstOrDefault(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, ChannelPortName, StringComparison.OrdinalIgnoreCase));

        string? fromNodeId;
        string? fromPort;

        if (conn is not null)
        {
            fromNodeId = conn.From;
            fromPort = conn.FromPort;
        }
        else
        {
            // Team agents have a channel node in their graph but no explicit port connection.
            var fallback = agent.Graph.Nodes.FirstOrDefault(n => NodeInstanceRegistry.GetPrototype(n.NodeType) is IHumanInteractionChannel);
            if (fallback is null)
                return null;
            fromNodeId = fallback.Id;
            fromPort = "resource";
        }

        var node = agent.Graph.FindNode(fromNodeId);
        if (node is null)
            return null;

        var channelNode = NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(node.NodeType);
        if (channelNode is null)
            return null;

        var resourceType = ResourceNodeRegistry.ResolveResourceType(node.NodeType) ?? ResourcePortCategory.InteractionChannel;
        var binding = new ResourceBinding(fromNodeId, fromPort, node, resourceType);

        // Channel type is declared by the node itself — not derived from the node type string.
        var channelType = channelNode.ChannelType;
        if (string.IsNullOrWhiteSpace(channelType))
            return null;

        return new ChannelBinding(ChannelType: channelType, Credentials: binding.GetString("credentials"), Respondent: binding.GetString("respondent"), Properties: binding);
    }

    /// <summary>
    /// Résout le nœud LLM model connecté à <paramref name="aiNodeId"/> via le port <c>llm_model</c>.
    /// Retourne null si aucun nœud modèle n'est connecté ou reconnu.
    /// </summary>
    public static (ILlmProvider Provider, ResourceBinding Binding)? ResolveLlmProvider(AgentEntity agent, string aiNodeId)
    {
        var conn = agent.Graph.Connections.FirstOrDefault(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, LlmModelPortName, StringComparison.OrdinalIgnoreCase));

        if (conn is null)
            return null;

        var node = agent.Graph.FindNode(conn.From);
        if (node is null)
            return null;

        var llmProvider = NodeInstanceRegistry.GetAs<ILlmProvider>(node.NodeType);
        if (llmProvider is null)
            return null;

        var resourceType = ResourceNodeRegistry.ResolveResourceType(node.NodeType) ?? ResourcePortCategory.LlmModel;
        var binding = new ResourceBinding(conn.From, conn.FromPort, node, resourceType);
        return (llmProvider, binding);
    }
}
