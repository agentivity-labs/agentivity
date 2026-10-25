namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public sealed record HumanLoopPolicyDecision(bool ShouldInteractWithHuman, string? BlockReason = null);
