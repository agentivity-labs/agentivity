namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// The ports of an agent step whose resources give it tools: its tools, and its knowledge (a Knowledge Base is searched by the
/// agent as a tool). Every place that lists or calls an agent's tools reads them from here.
/// </summary>
public static class AgentToolPorts
{
    public static readonly IReadOnlyList<(string Port, string ResourceType)> All = [("tools", ResourcePortCategory.AIAgentTool), ("knowledge", ResourcePortCategory.KnowledgeSource)];
}
