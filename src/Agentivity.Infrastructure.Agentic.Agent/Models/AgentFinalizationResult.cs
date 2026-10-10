namespace Agentivity.Infrastructure.Agentic.Agent;

internal sealed record AgentFinalizationResult(string? FinalResponse, string? FinishReason = null, bool ContinueExecution = false);
