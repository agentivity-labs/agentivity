namespace Agentivity.Infrastructure.Agentic.Agent;

internal sealed record AgentWorkspace(List<AgentWorkspaceEntry> Goals, List<AgentWorkspaceEntry> Facts, List<AgentWorkspaceEntry> Constraints, List<AgentWorkspaceEntry> Decisions, List<AgentWorkspaceEntry> ToolFindings, List<AgentWorkspaceEntry> OpenQuestions)
{
    public bool HasContent => Goals.Count > 0 || Facts.Count > 0 || Constraints.Count > 0 || Decisions.Count > 0 || ToolFindings.Count > 0 || OpenQuestions.Count > 0;
}

internal sealed record AgentWorkspaceEntry(string Key, string Value);

internal sealed record HumanLoopResolution(List<AgentWorkspaceEntry> ResolvedQuestions, List<AgentWorkspaceEntry> Facts, List<AgentWorkspaceEntry> Constraints, List<AgentWorkspaceEntry> Decisions)
{
    public static HumanLoopResolution Empty { get; } = new([], [], [], []);
}
