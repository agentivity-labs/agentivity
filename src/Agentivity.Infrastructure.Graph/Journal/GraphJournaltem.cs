namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Base record for any event appended to a <see cref="GraphJournal"/> during graph execution.
/// Subclass in the calling layer to attach domain-specific data.
/// </summary>
/// <param name="OccurredAtUtc">UTC timestamp when the event occurred.</param>
/// <param name="GraphId">Identifier of the graph this item belongs to.</param>
/// <param name="TaskId">
/// The task that produced this item, or <c>null</c> for graph-level events
/// (e.g. graph initialized, plan revised).
/// </param>
/// <param name="ItemKind">
/// Free-form discriminator string (e.g. <c>"task_completed"</c>, <c>"graph_initialized"</c>).
/// </param>
public abstract record GraphJournaltem(DateTimeOffset OccurredAtUtc, string GraphId, string? TaskId, string ItemKind);
