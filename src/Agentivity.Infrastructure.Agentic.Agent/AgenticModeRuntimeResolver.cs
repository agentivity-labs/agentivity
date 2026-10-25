namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record ResolvedAgenticEngineRuntime(IAgentEngine Engine, IAgenticModeRuntime ModeRuntime);

public static class AgenticModeRuntimeResolver
{
    public static ResolvedAgenticEngineRuntime Resolve(IEnumerable<IAgentEngine> engines, string agentMode, string? requestedEngineId = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        foreach (var engine in engines)
        {
            if (!string.IsNullOrWhiteSpace(requestedEngineId) && !string.Equals(engine.Id, requestedEngineId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (engine.TryGetModeRuntime(agentMode, out var modeRuntime) && modeRuntime is not null)
            {
                return new ResolvedAgenticEngineRuntime(engine, modeRuntime);
            }
        }

        return string.IsNullOrWhiteSpace(requestedEngineId) ? throw new InvalidOperationException($"No registered agentic engine can handle mode '{agentMode}'.") : throw new InvalidOperationException($"Agentic engine '{requestedEngineId}' does not support mode '{agentMode}'.");
    }

    public static ResolvedAgenticEngineRuntime Resolve(IAgentEngine engine, string agentMode) => Resolve([engine], agentMode, requestedEngineId: engine.Id);
}
