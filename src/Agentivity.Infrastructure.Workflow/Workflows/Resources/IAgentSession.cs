using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Workflows.Resources;

public interface IAgentSession
{
    ValueTask<AgentSessionState> OpenAsync(ResourceBinding binding, AgentSessionOpenRequest request, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(ResourceBinding binding, AgentSessionState session, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(ResourceBinding binding, AgentSessionOpenRequest request, CancellationToken cancellationToken = default);
}

public interface IAgentSessionAdmin
{
    ValueTask<IReadOnlyList<AgentSessionSummary>> ListAsync(ResourceBinding binding, CancellationToken cancellationToken = default);

    ValueTask<int> PurgeExpiredAsync(ResourceBinding binding, CancellationToken cancellationToken = default);
}

public sealed record AgentSessionOpenRequest(string SessionId, string AgentNodeId, string? ScopeHint = null);

/// <param name="Transcript">Rolling prose summary of messages older than <paramref name="TranscriptUpToIndex"/> — see <c>SummaryStep</c>/<c>AgentSessionCompaction</c>.</param>
/// <param name="TranscriptUpToIndex">How far into the session's full message history <paramref name="Transcript"/> already covers.</param>
public sealed record AgentSessionState(string SessionId, string Scope, List<LlmChatMessage> Messages, Dictionary<string, JsonObject> ToolState, Dictionary<string, object?> Metadata, DateTimeOffset UpdatedAt, int TranscriptUpToIndex = 0);

public sealed record AgentSessionSummary(string SessionId, string Scope, int MessageCount, int ToolStateCount, int MetadataCount, DateTimeOffset UpdatedAt, bool IsExpired);
