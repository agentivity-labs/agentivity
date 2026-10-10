using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Execution;

namespace Agentivity.Infrastructure.Agentic.Agent;

public interface IAgenticModeRuntime : IExecutionUnit<AgentConfig, AgentRunResult>
{
    string ModeId { get; }
    string DisplayName { get; }
    AgenticModeCapabilities Capabilities { get; }
    AgentPolicySet DefaultPolicies { get; }
    IReadOnlyList<RuntimePolicyDescriptor> PolicyLibrary { get; }

    /// <summary>
    /// Mode-specific runtime parameters keyed by <see cref="ArgumentDefinition.Name"/>.
    /// Each mode runtime declares its own parameters;
    /// the workflow UI presents them under a tab named after <see cref="DisplayName"/>.
    /// </summary>
    IReadOnlyDictionary<string, ArgumentDefinition> RuntimeArguments { get; }

    /// <summary>
    /// Autonomous execution entry point. Receives resolved config, mutable run state,
    /// agent-specific runtime services, and the agent host for policy/guardrail evaluation.
    /// All runtimes implement this.
    /// </summary>
    ValueTask<AgentRunResult> ExecuteAsync(AgentConfig config, AgentRunState state, AgentRunServices services, AgentHost agentHost, CancellationToken cancellationToken);

    // ── IExecutionUnit<AgentConfig, AgentRunResult> bridge ───────────────────
    // The generic IExecutionUnit contract. Callers holding a typed reference
    // always use ExecuteAsync(AgentConfig, AgentRunState, AgentRunServices, ct).
    // This bridge exists to satisfy the interface — it is not the primary call path.
    ValueTask<AgentRunResult> IExecutionUnit<AgentConfig, AgentRunResult>.ExecuteAsync(AgentConfig config, IRunService services, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Call ExecuteAsync(AgentConfig, AgentRunState, AgentRunServices, ct) directly. " + "AgentRunState must be supplied by the execution host.");
}
