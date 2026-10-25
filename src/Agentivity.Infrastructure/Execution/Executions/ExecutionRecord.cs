using System;
using System.Collections.Generic;
using System.Text;

namespace Agentivity.Infrastructure.Execution.Executions;

public sealed record ExecutionRecord
{
    public required string ExecutionId { get; init; }
    public required string EntityId { get; init; }
    public required string EntityKind { get; init; } // "agent" | "team" | "workflow"
    public required string Input { get; init; }
    public required string State { get; init; } // "active" | "paused" | "completed" | "failed"
    public Dictionary<string, string> Channels { get; init; } = []; // channelType → threadId
    public string? CurrentRunId { get; init; }
    public List<string> RunIds { get; init; } = [];

    /// <summary>
    /// SSE channel identifier — from <c>ExecutionContext.StreamId</c>.
    /// Persisted so the channel can be reconstructed when resuming a paused execution.
    /// </summary>
    public string? StreamId { get; init; }

    /// <summary>
    /// Chat thread identifier — independent from RunId, ExecutionId and StreamId.
    /// Persisted so the interaction thread can be restored on HIL resume without creating a new thread.
    /// </summary>
    public string? InteractionThreadId { get; init; }

    /// <summary>
    /// Who started it (the person signed in, or the owner of an instance without sign-in): what it runs runs as them, and a resume
    /// keeps it. Null for an execution kept before it was noted, or started from within the platform (not by a request).
    /// </summary>
    public Agentivity.Infrastructure.Identity.Caller? StartedBy { get; init; }

    /// <summary>The space it belongs to: the space of what it runs.</summary>
    public string SpaceId { get; init; } = Agentivity.Infrastructure.Storage.Workspace.WorkspaceSpaces.Default;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
