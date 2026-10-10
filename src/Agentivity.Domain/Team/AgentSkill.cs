namespace Agentivity.Domain.Team;

/// <summary>
/// A non-executable capability description attached to an agent.
/// Skills are informational only and contribute to prompt generation.
/// </summary>
public sealed record AgentSkill(string Name, string Description);
