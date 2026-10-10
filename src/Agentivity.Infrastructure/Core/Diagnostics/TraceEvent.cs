namespace Agentivity.Infrastructure.Core.Diagnostics;

/// <summary>
/// Base abstraite d'un événement de trace. Chaque event est auto-descriptif :
/// il sait produire son propre <see cref="DebugTraceItem"/> sans formatter externe.
/// </summary>
public abstract record TraceEvent(int Iteration)
{
    public abstract DebugTraceItem ToDebugTraceItem();
}
