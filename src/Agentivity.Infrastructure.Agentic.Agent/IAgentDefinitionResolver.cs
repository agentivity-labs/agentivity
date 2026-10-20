using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Bundles every value <see cref="AgentHost.RunAgentAsync"/> needs that is resolved
/// "upstream" of the run — i.e. before the four <c>IAgent*Proxy</c> instances and the
/// session are loaded.
/// <para>
/// Produced by <see cref="IAgentDefinitionResolver"/> implementations:
/// <c>WorkflowAgentDefinitionResolver</c> (NodeConfig/<c>AgentEntity</c>-graph based, used
/// by <c>AiNode</c>) and <c>StandaloneAgentDefinitionResolver</c> (<c>AgentEntity.Graph</c>
/// based, used by <c>StandaloneAgent</c>/<c>TeamAgent</c>).
/// </para>
/// </summary>
public sealed record ResolvedAgentDefinition(
    /// <summary>The mode runtime selected for this run (e.g. "conversational").</summary>
    IAgenticModeRuntime ModeRuntime,
    /// <summary>Mode-specific runtime arguments resolved from the agent/node configuration.</summary>
    IReadOnlyDictionary<string, ArgumentDefinition> RuntimeArguments,
    /// <summary>Normalized runtime knobs (temperature, max tokens, max iterations, tool choice, etc.).</summary>
    AgentRuntimeParameters RuntimeParameters,
    /// <summary>Resolved policy layers (mode defaults + connected policy resources).</summary>
    ResolvedAgentPolicies ResolvedPolicies,
    /// <summary>Resolved guardrail definitions (connected guardrail resources).</summary>
    IReadOnlyList<AgentGuardrailDefinition> ResolvedGuardrails,
    /// <summary>The fully-resolved user prompt (template-substituted, or the raw "input" argument).</summary>
    string ResolvedPrompt,
    /// <summary>The system prompt, or <see cref="string.Empty"/> when none is configured.</summary>
    string SystemPrompt,
    /// <summary>Whether the session should be persisted at the end of the run.</summary>
    bool PersistSession,
    /// <summary>The LLM execution runtime port used to invoke the model (already wired to the resolved provider/binding).</summary>
    IAgentLlmProxy RuntimePort,
    /// <summary>Platform services (correlation, checkpoint/state/trace delegates) passed to <c>AgentHost.RunAgentAsync</c>.</summary>
    AgentRunServices ContextServices,
    /// <summary>Images attached to the resolved prompt for a vision-capable model, or <c>null</c> when none.</summary>
    IReadOnlyList<ImageAttachment>? Images = null,
    /// <summary>The format the final answer must have (a JSON Schema, the agent's <c>response_schema</c>), or empty when it has none.</summary>
    string ResponseSchema = ""
);

/// <summary>
/// Resolves all the "upstream" values needed by <see cref="AgentHost.RunAgentAsync"/> from the
/// generic <c>(args, context)</c> shell-facing inputs.
/// <para>
/// Two implementations exist — one per upstream source of agent definitions:
/// <list type="bullet">
///   <item><description><c>WorkflowAgentDefinitionResolver</c> (<c>Agentivity.Infrastructure.Workflow.Agentic</c>) — resolves from a workflow <c>NodeConfig</c>.</description></item>
///   <item><description><c>StandaloneAgentDefinitionResolver</c> (<c>Agentivity.Infrastructure.Agentic.Composables</c>) — resolves from an <c>AgentEntity.Graph</c>.</description></item>
/// </list>
/// </para>
/// This interface lives in <c>Agentivity.Infrastructure.Agentic.Agent</c> and has zero
/// dependency on <c>Agentivity.Infrastructure.Workflow.*</c> types.
/// </summary>
public interface IAgentDefinitionResolver
{
    ValueTask<ResolvedAgentDefinition> ResolveAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default);
}

/// <summary>
/// Adapts a plain async function to <see cref="IAgentDefinitionResolver"/> — lets shells implement
/// resolution as a simple method/local function instead of a dedicated class.
/// </summary>
public sealed class DelegateAgentDefinitionResolver(Func<IReadOnlyDictionary<string, object?>, RunContext, CancellationToken, ValueTask<ResolvedAgentDefinition>> resolve) : IAgentDefinitionResolver
{
    public ValueTask<ResolvedAgentDefinition> ResolveAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default) => resolve(args, context, ct);
}
