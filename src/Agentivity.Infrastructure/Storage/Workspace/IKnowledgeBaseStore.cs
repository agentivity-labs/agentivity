namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>A document of a Knowledge Base as it was read: its key in its source, the fingerprint of what was read, its title and where it comes from.</summary>
public sealed record KnowledgeBaseDocumentRecord(string Key, string Fingerprint, string? Title, string? Source, DateTimeOffset SyncedAt);

/// <summary>A passage of a document: its place in the document, its text, its vector, and what is known of it (its section…).</summary>
public sealed record KnowledgeBasePassageRecord(int Ordinal, string Text, float[] Vector, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A passage found by a search, with the document it comes from and how close it is to the question (the higher, the closer).</summary>
public sealed record KnowledgeBaseMatch(string DocumentKey, int Ordinal, string Text, string? Title, string? Source, double Score, IReadOnlyDictionary<string, string>? Metadata);

public sealed record KnowledgeBaseCounts(int Documents, int Passages);

/// <summary>A document a base holds, as it is shown: its key, title, where it comes from, when it was read, how many passages it gave.</summary>
public sealed record KnowledgeBaseDocumentSummary(string Key, string? Title, string? Source, DateTimeOffset SyncedAt, int Passages);

/// <summary>A source that could not be read during a synchronisation, and why.</summary>
public sealed record KnowledgeBaseSyncError(string Source, string Message);

/// <summary>
/// Where the last synchronisation of a base stands: syncing, synced or failed, when it ran, what it holds, what went wrong, and the
/// change stamp its source gave then (to tell, later, whether the source changed since).
/// </summary>
public sealed record KnowledgeBaseSyncState(string Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int Documents, int Passages, IReadOnlyList<KnowledgeBaseSyncError> Errors, string? ChangeStamp = null);

/// <summary>
/// What the Knowledge Bases keep in the workspace's database (SQLite, Postgres): their documents, their passages with their
/// vectors, and the state of their last synchronisation. Each base is apart from the others. It is the store of the engine of the
/// bases: nobody else reads or writes it.
/// </summary>
public interface IKnowledgeBaseStore
{
    /// <summary>The documents of the base, by key, with the fingerprint of what was read: what a synchronisation compares with.</summary>
    ValueTask<IReadOnlyDictionary<string, string>> FingerprintsAsync(string baseId, CancellationToken cancellationToken = default);

    /// <summary>Writes a document and its passages, in place of what the base held for that key, all at once.</summary>
    ValueTask WriteDocumentAsync(string baseId, KnowledgeBaseDocumentRecord document, IReadOnlyList<KnowledgeBasePassageRecord> passages, CancellationToken cancellationToken = default);

    /// <summary>Removes these documents and their passages.</summary>
    ValueTask RemoveDocumentsAsync(string baseId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>Removes every document and passage of the base, and the state of its last synchronisation.</summary>
    ValueTask EmptyAsync(string baseId, CancellationToken cancellationToken = default);

    ValueTask<KnowledgeBaseCounts> CountAsync(string baseId, CancellationToken cancellationToken = default);

    /// <summary>The documents the base holds, by title.</summary>
    ValueTask<IReadOnlyList<KnowledgeBaseDocumentSummary>> ListDocumentsAsync(string baseId, CancellationToken cancellationToken = default);

    /// <summary>The <paramref name="limit"/> passages of the base closest to <paramref name="query"/> (a vector), the closest first.</summary>
    ValueTask<IReadOnlyList<KnowledgeBaseMatch>> SearchAsync(string baseId, float[] query, int limit, CancellationToken cancellationToken = default);

    ValueTask SaveStateAsync(string baseId, KnowledgeBaseSyncState state, CancellationToken cancellationToken = default);

    /// <summary>The state of the last synchronisation, or null when the base was never synchronised.</summary>
    ValueTask<KnowledgeBaseSyncState?> StateAsync(string baseId, CancellationToken cancellationToken = default);
}
