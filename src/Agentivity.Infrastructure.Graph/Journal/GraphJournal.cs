using System.Collections.Immutable;

namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Append-only ordered log of <see cref="GraphJournaltem"/> entries for a single graph
/// execution. The journal is a passive data structure — it records what happened but
/// drives nothing.
/// </summary>
/// <remarks>
/// <para>
/// The graph engine appends <see cref="GraphTaskCompleted"/> and <see cref="GraphTaskFailed"/>
/// items automatically. Callers append domain-specific items (e.g. plan steps, tool calls)
/// via <see cref="Append"/>.
/// </para>
/// <para>
/// Thread safety: <see cref="GraphJournal"/> is not thread-safe. Callers running inside the
/// graph scheduling loop already hold the execution lock; external appends must be serialised
/// by the caller.
/// </para>
/// </remarks>
public sealed class GraphJournal
{
    private readonly List<GraphJournaltem> _items = [];

    // ─── Read ───────────────────────────────────────────────────────────────────

    /// <summary>All items in append order.</summary>
    public IReadOnlyList<GraphJournaltem> Items => _items;

    /// <summary>Number of items in the journal.</summary>
    public int Count => _items.Count;

    // ─── Write ──────────────────────────────────────────────────────────────────

    /// <summary>Appends a single item to the journal.</summary>
    public void Append(GraphJournaltem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    /// <summary>Appends multiple items to the journal.</summary>
    public void AppendRange(IEnumerable<GraphJournaltem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    // ─── Query ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all items matching the given <paramref name="eventKind"/> discriminator.
    /// </summary>
    public IEnumerable<GraphJournaltem> OfKind(string eventKind) => _items.Where(i => string.Equals(i.ItemKind, eventKind, StringComparison.Ordinal));

    /// <summary>
    /// Returns all items produced by the given <paramref name="taskId"/>.
    /// </summary>
    public IEnumerable<GraphJournaltem> ForTask(string taskId) => _items.Where(i => string.Equals(i.TaskId, taskId, StringComparison.Ordinal));

    /// <summary>
    /// Returns a snapshot of the current items as an immutable list.
    /// </summary>
    public ImmutableList<GraphJournaltem> Snapshot() => [.. _items];
}
