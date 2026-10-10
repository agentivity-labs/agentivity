namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Well-known <see cref="GraphJournaltem.ItemKind"/> discriminator constants emitted
/// by the graph engine. Domain layers may define their own constants alongside these.
/// </summary>
public static class GraphJournalItemKinds
{
    public const string TaskCompleted = "task_completed";
    public const string TaskFailed = "task_failed";
    public const string GraphSubmitted = "graph_submitted";
    public const string GraphFinished = "graph_finished";
}
