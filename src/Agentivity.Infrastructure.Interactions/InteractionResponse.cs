using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Response to an interaction request, provided by any channel.
/// </summary>
public sealed record InteractionResponse
{
    public required Guid RequestId { get; init; }
    public required InteractionOutcome Outcome { get; init; }
    public bool Success { get; init; } = true;
    public string? ResponseText { get; init; }
    public string? MessageId { get; init; }
    public string? ThreadId { get; init; }
    public string? ConversationId { get; init; }
    public IReadOnlyList<InteractionMessage> Messages { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
    public string? Channel { get; init; }
    public string? RespondedBy { get; init; }
    public DateTime RespondedAt { get; init; } = DateTime.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionOutcome
{
    Submitted,
    Approved,
    Rejected,
    Revised,
    TimedOut,
    Cancelled,
}
