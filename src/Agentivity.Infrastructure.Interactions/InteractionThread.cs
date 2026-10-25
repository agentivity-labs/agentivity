using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Interactions;

public sealed record InteractionThread
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    /// <summary>
    /// Execution (session) this thread belongs to.
    /// Propagated from <c>RunContext.ExecutionId</c> at thread creation time.
    /// </summary>
    public string? ExecutionId { get; init; }

    /// <summary>
    /// SSE channel identifier — from <c>ExecutionContext.StreamId</c>.
    /// Used to route thread events to the correct frontend channel subscription.
    /// Distinct from <see cref="RunId"/>, <see cref="ExecutionId"/>, and <see cref="Id"/> (ThreadId).
    /// </summary>
    public string? StreamId { get; init; }

    public required string Title { get; init; }

    public bool IsDefault { get; init; }

    public InteractionThreadStatus Status { get; init; } = InteractionThreadStatus.Open;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionThreadStatus
{
    Open,
    Closed,
    Archived,
}
