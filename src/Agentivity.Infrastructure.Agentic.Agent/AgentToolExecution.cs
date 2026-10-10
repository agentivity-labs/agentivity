using Agentivity.Infrastructure.Core.Diagnostics;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record AgentToolExecution(string ToolCallId, string ToolName, string ArgumentsJson, string ResultContent, bool IsError, bool ReusedFromToolState, DebugTraceItem? Debug = null, string? FailedReason = null);
