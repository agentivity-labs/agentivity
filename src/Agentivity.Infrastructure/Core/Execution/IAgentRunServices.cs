namespace Agentivity.Infrastructure.Core.Execution;

/// <summary>
/// Marker contract for runtime-services bundles usable by agent execution
/// (<c>AgentExecutable</c> / <c>AgentHost</c>) regardless of which shell
/// (workflow node, standalone agent, team) is hosting the run.
/// <para>
/// Currently identical in shape to <see cref="IRunService"/> — the universal
/// runtime services contract — but kept as a distinct interface so that
/// agent-specific members can be added here over time (STORY-0459.6b/c/d)
/// without widening <see cref="IRunService"/> for non-agent engines.
/// </para>
/// <para>
/// Implemented by <c>WorkflowRunServices</c> (Agentivity.Infrastructure.Workflow)
/// and <c>AgentRunServices</c> (Agentivity.Infrastructure.Agentic.Agent).
/// </para>
/// </summary>
public interface IAgentRunServices : IRunService { }
