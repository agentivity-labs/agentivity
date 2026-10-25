using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record SessionContext(IAgentSession? Session, ResourceBinding? Binding, string SessionId, string Scope, bool Restored, IReadOnlyList<LlmChatMessage> RestoredMessages, AgentSessionState State)
{
    /// <summary>
    /// How far into the session's full message history the rolling summary
    /// (<see cref="Agentivity.Infrastructure.Execution.Runs.RunContext.LastTranscriptSummary"/>)
    /// already covers. Stays on the session rather than the run's <c>TranscriptScope</c>: it's an
    /// offset into THIS turn's own message list, which has no consistent meaning inside a scope
    /// that may be shared across many different team members' distinct message lists.
    /// </summary>
    public int TranscriptUpToIndex { get; set; } = State.TranscriptUpToIndex;

    public static SessionContext Disabled { get; } =
        new(null, null, string.Empty, string.Empty, false, [], new AgentSessionState(string.Empty, string.Empty, [], new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.MinValue));

    public bool Enabled => Session is not null && Binding is not null;
}
