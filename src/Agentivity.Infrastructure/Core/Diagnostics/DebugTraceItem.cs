// DebugTraceItem, DebugTraceField and DebugTraceStatus have moved to Agentivity.Domain.Run.
// Re-export them here so existing Infrastructure code continues to compile without changes.
global using DebugTraceField = Agentivity.Domain.Run.DebugTraceField;
global using DebugTraceItem = Agentivity.Domain.Run.DebugTraceItem;
global using DebugTraceStatus = Agentivity.Domain.Run.DebugTraceStatus;
