using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Arguments;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record AgenticModeCapabilities(
    bool UsesConnectedTools = false,
    bool AllowsToolChoiceConfiguration = false,
    bool SanitizesObservations = false,
    /// <summary>
    /// When true, the coordinator emits granular per-step trace events
    /// (react_thought, react_action, react_observation) for each ReAct
    /// reasoning step. Used for incident reconstruction and observability.
    /// </summary>
    bool EmitsReActStepTrace = false
)
{
    public static AgenticModeCapabilities None { get; } = new();
}

public abstract class AgenticModeRuntimeBase : IAgenticModeRuntime
{
    public string ModeId { get; }
    public string DisplayName { get; }
    public AgenticModeCapabilities Capabilities { get; }
    public AgentPolicySet DefaultPolicies { get; }
    public virtual IReadOnlyList<RuntimePolicyDescriptor> PolicyLibrary { get; } = [];
    public virtual IReadOnlyDictionary<string, ArgumentDefinition> RuntimeArguments { get; } = new Dictionary<string, ArgumentDefinition>();

    public AgenticModeRuntimeBase(string modeId, string displayName, AgenticModeCapabilities? capabilities = null, AgentPolicySet? defaultPolicies = null)
    {
        this.ModeId = modeId;
        this.DisplayName = displayName;
        this.Capabilities = capabilities ?? AgenticModeCapabilities.None;
        this.DefaultPolicies = defaultPolicies ?? new AgentPolicySet();
    }

    public abstract ValueTask<AgentRunResult> ExecuteAsync(AgentConfig config, AgentRunState state, AgentRunServices services, AgentHost agentHost, CancellationToken cancellationToken);

    protected AgentManifest BuildManifest(AgentHost agentHost, AgentConfig config, AgentRunState state, AgentRunServices services, HumanReviewResumeState? resumeState = null) => new(agentHost, Capabilities, config, state, resumeState ?? HumanReviewResumeState.None, services);
}
