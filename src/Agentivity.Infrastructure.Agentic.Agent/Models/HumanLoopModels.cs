using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record HumanLoopDuplicateReviewAssessment
{
    [JsonPropertyName("should_block")]
    public bool ShouldBlock { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("reuse_guidance")]
    public string ReuseGuidance { get; init; } = string.Empty;
}

/// <summary>
/// Unwrapped result of a plain-text turn parsed against <see cref="AgentSchemas.ConversationalReply"/>.
/// </summary>
public sealed record ConversationalReply(string? Content, bool NeedsHumanInput, bool IsSelectionQuestion, bool IsRepeatOfPreviousTurn = false);

public enum HumanLoopClarificationResolutionStatus
{
    Unknown,
    Approximate,
    Exact,
    NoPreference,
    Flexible,
    Declined,
    Contradictory,
    Superseded,
}

public enum HumanLoopClarificationReAskPolicy
{
    BlockReAsk,
    AllowIfContradicted,
    AllowIfToolRequiresExact,
    AllowIfExpired,
    Allow,
}

public sealed record HumanLoopClarificationState
{
    public string EntryId { get; init; } = Guid.NewGuid().ToString("N");

    public string ClarificationKey { get; init; } = string.Empty;

    public string QuestionText { get; init; } = string.Empty;

    public string RawHumanAnswer { get; init; } = string.Empty;

    public string NormalizedSummary { get; init; } = string.Empty;

    public HumanLoopClarificationResolutionStatus ResolutionStatus { get; init; }

    public HumanLoopClarificationReAskPolicy ReAskPolicy { get; init; }

    public int ResolvedAtTurn { get; init; }

    public int LastUpdatedAtTurn { get; init; }

    public string? Supersedes { get; init; }

    public string? SupersededBy { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsActive => string.IsNullOrWhiteSpace(SupersededBy) && ResolutionStatus != HumanLoopClarificationResolutionStatus.Superseded;
}
