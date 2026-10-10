using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Interactions;

public sealed record ThreadMessage
{
    public required Guid Id { get; init; }

    public required Guid ThreadId { get; init; }

    public required Guid RunId { get; init; }

    public ThreadAuthorType AuthorType { get; init; } = ThreadAuthorType.User;

    public string? AuthorId { get; init; }

    public string? AuthorName { get; init; }

    public required string Text { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThreadAuthorType
{
    User,
    Agent,
    System,
    Workflow,
}
