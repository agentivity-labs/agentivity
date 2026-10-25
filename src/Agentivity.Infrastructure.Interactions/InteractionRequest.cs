using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Represents a request for external input during a run (workflow, agent, team…).
/// Channel-agnostic: describes WHAT is needed, not HOW to collect it.
/// </summary>
public sealed record InteractionRequest
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public required string NodeId { get; init; }
    public required InteractionKind Kind { get; init; }
    public HumanInteractionOperation Operation { get; init; } = HumanInteractionOperation.SendAndWait;
    public required string ChannelType { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? Message { get; init; }
    public string? MessageId { get; init; }
    public string? ThreadId { get; init; }
    public string? ConversationId { get; init; }
    public string? Query { get; init; }
    public IReadOnlyList<InteractionField> Fields { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan? Timeout { get; init; }
    public string? Respondent { get; init; }

    /// <summary>
    /// Execution (session) identifier propagated from <c>RunContext.ExecutionId</c>.
    /// Allows the interaction channel to associate the created thread with the session.
    /// </summary>
    public string? ExecutionId { get; init; }

    /// <summary>
    /// SSE channel identifier — from <c>ExecutionContext.StreamId</c>.
    /// Routes HIL events to the correct frontend channel stream.
    /// Distinct from <see cref="RunId"/> and <see cref="ExecutionId"/>.
    /// </summary>
    public string? StreamId { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HumanInteractionOperation
{
    Send,
    SendAndWait,
    Receive,
    Update,
    Delete,
    Search,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionKind
{
    /// <summary>Generic free-text human interaction.</summary>
    Message,

    /// <summary>Collect structured data from user.</summary>
    Input,

    /// <summary>Yes/No/Revise decision.</summary>
    Approval,

    /// <summary>Select from predefined options.</summary>
    Choice,

    /// <summary>Informational only, no response expected.</summary>
    Notification,
}

public sealed record InteractionMessage
{
    public string? MessageId { get; init; }
    public string? ThreadId { get; init; }
    public string? ConversationId { get; init; }
    public string? AuthorId { get; init; }
    public string? AuthorName { get; init; }
    public string? Text { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed record InteractionField
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required InteractionFieldType Type { get; init; }
    public bool Required { get; init; } = true;
    public object? DefaultValue { get; init; }
    public string? Hint { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionFieldType
{
    Text,
    MultilineText,
    Number,
    Boolean,
    Choice,
    Date,
    File,
}
