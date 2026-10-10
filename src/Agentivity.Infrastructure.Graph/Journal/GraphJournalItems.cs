namespace Agentivity.Infrastructure.Graph;

/// <summary>Appended by the graph engine when a task completes successfully.</summary>
public sealed record GraphTaskCompletedItem(DateTimeOffset OccurredAtUtc, string GraphId, string TaskId, object? Output) : GraphJournaltem(OccurredAtUtc, GraphId, TaskId, GraphJournalItemKinds.TaskCompleted);

/// <summary>Appended by the graph engine when a task fails.</summary>
public sealed record GraphTaskFailedItem(DateTimeOffset OccurredAtUtc, string GraphId, string TaskId, string? Reason) : GraphJournaltem(OccurredAtUtc, GraphId, TaskId, GraphJournalItemKinds.TaskFailed);

/// <summary>Appended by the graph engine when a graph is submitted (first activation).</summary>
public sealed record GraphSubmittedItem(DateTimeOffset OccurredAtUtc, string GraphId) : GraphJournaltem(OccurredAtUtc, GraphId, null, GraphJournalItemKinds.GraphSubmitted);

/// <summary>Appended by the graph engine when a graph finishes (terminal state reached).</summary>
public sealed record GraphFinishedItem(DateTimeOffset OccurredAtUtc, string GraphId, GraphTaskStatus OverallStatus) : GraphJournaltem(OccurredAtUtc, GraphId, null, GraphJournalItemKinds.GraphFinished);
