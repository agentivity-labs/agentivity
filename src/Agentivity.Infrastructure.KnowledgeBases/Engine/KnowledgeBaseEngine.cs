using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Sources;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.KnowledgeBases.Engine;

/// <summary>A base that cannot answer: it holds nothing and its source could not be read. The message says why.</summary>
public sealed class KnowledgeBaseUnavailableException(string message) : Exception(message);

/// <summary>
/// The engine of the Knowledge Bases. A synchronisation reads the base's source, cuts each document into passages
/// (<see cref="KnowledgeChunker"/>), turns them into vectors and keeps them (<see cref="IKnowledgeBaseStore"/>); a document whose
/// text did not change is not treated again, one that disappeared from the source is removed. A search turns the question into a
/// vector and gives the closest passages. How the base is kept up to date is its own setting (<see cref="KnowledgeBaseSync"/>);
/// a base never synchronised is at its first search. Everything here — cutting, vectors, index — is the engine's business:
/// nothing outside it reads or writes what a base holds.
/// </summary>
public sealed class KnowledgeBaseEngine(IKnowledgeBaseStore store, IEnumerable<IKnowledgeSourceType> sourceTypes, ITextEmbedder embedder, TimeProvider clock, ILogger<KnowledgeBaseEngine> logger)
{
    private readonly IReadOnlyDictionary<string, IKnowledgeSourceType> _types = sourceTypes.ToDictionary(t => t.TypeId, StringComparer.OrdinalIgnoreCase);

    // One synchronisation of a base at a time: a second one waits for the first, then finds nothing left to do.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The types of data source this installation can read.</summary>
    public IReadOnlyCollection<IKnowledgeSourceType> SourceTypes => [.. _types.Values];

    public async ValueTask<KnowledgeBaseSyncState> SyncAsync(KnowledgeBaseEntity knowledgeBase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        var running = Running.GetOrAdd(knowledgeBase.Id, _ => new SemaphoreSlim(1, 1));
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = clock.GetUtcNow();
        try
        {
            return await SyncNowAsync(knowledgeBase, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever stopped it, the base does not stay "syncing": it says it failed, and why, and can be synchronised again.
            logger.LogWarning(ex, "Knowledge base {Id}: the synchronisation stopped", knowledgeBase.Id);
            await FailAsync(knowledgeBase, started, knowledgeBase.SourceType, $"The synchronisation stopped: {ex.Message}", null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            running.Release();
        }
    }

    // A synchronisation of this base goes on in this server now.
    private static bool SyncingHere(string id) => Running.TryGetValue(id, out var running) && running.CurrentCount == 0;

    /// <summary>
    /// What a "syncing" state says when no synchronisation of the base goes on in this server: it was cut off (the server stopped
    /// in the middle). The documents it had treated are kept; the base is due again.
    /// </summary>
    private KnowledgeBaseSyncState? Settled(KnowledgeBaseEntity knowledgeBase, KnowledgeBaseSyncState? state) =>
        state is { Status: KnowledgeBaseStatus.Syncing } && !SyncingHere(knowledgeBase.Id)
            ? state with
            {
                Status = KnowledgeBaseStatus.Failed,
                FinishedAt = state.StartedAt,
                Errors = [new KnowledgeBaseSyncError(knowledgeBase.SourceType, "The synchronisation was cut off when the server stopped. What it had read is kept; synchronise it again to finish.")],
            }
            : state;

    /// <summary>The <paramref name="limit"/> passages closest to <paramref name="question"/>, the base brought up to date first when it is due.</summary>
    public async ValueTask<IReadOnlyList<KnowledgeBaseMatch>> RetrieveAsync(KnowledgeBaseEntity knowledgeBase, string question, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var state = await store.StateAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        var cutOff = state is { Status: KnowledgeBaseStatus.Syncing } && !SyncingHere(knowledgeBase.Id);
        if (cutOff || await IsDueAsync(knowledgeBase, state, cancellationToken).ConfigureAwait(false))
            state = await SyncAsync(knowledgeBase, cancellationToken).ConfigureAwait(false);

        var counts = await store.CountAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        if (counts.Passages == 0 && state is { Status: KnowledgeBaseStatus.Failed })
            throw new KnowledgeBaseUnavailableException($"The knowledge base '{knowledgeBase.Name}' holds nothing yet: {string.Join(" ", state.Errors.Select(e => e.Message))}");

        var vector = (await embedder.EmbedAsync([question], cancellationToken).ConfigureAwait(false))[0];
        return await store.SearchAsync(knowledgeBase.Id, vector, Math.Clamp(limit, 1, 50), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes everything the base holds; its source and settings stay, and the next search synchronises it again.</summary>
    public ValueTask EmptyAsync(KnowledgeBaseEntity knowledgeBase, CancellationToken cancellationToken = default) => store.EmptyAsync(knowledgeBase.Id, cancellationToken);

    /// <summary>The documents the base holds, by title.</summary>
    public ValueTask<IReadOnlyList<KnowledgeBaseDocumentSummary>> DocumentsAsync(KnowledgeBaseEntity knowledgeBase, CancellationToken cancellationToken = default) => store.ListDocumentsAsync(knowledgeBase.Id, cancellationToken);

    /// <summary>What the base holds and how its last synchronisation went.</summary>
    public async ValueTask<(KnowledgeBaseCounts Counts, KnowledgeBaseSyncState? State)> StatusAsync(KnowledgeBaseEntity knowledgeBase, CancellationToken cancellationToken = default) =>
        (await store.CountAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false), Settled(knowledgeBase, await store.StateAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false)));

    private async ValueTask<bool> IsDueAsync(KnowledgeBaseEntity knowledgeBase, KnowledgeBaseSyncState? state, CancellationToken cancellationToken)
    {
        if (state is null)
            return true;
        if (state.Status == KnowledgeBaseStatus.Syncing)
            return false;

        var since = clock.GetUtcNow() - (state.FinishedAt ?? state.StartedAt ?? DateTimeOffset.MinValue);
        var expired = since >= TimeSpan.FromHours(Math.Max(1, knowledgeBase.Sync.ExpiryHours));
        switch (knowledgeBase.Sync.Mode)
        {
            case KnowledgeBaseSync.Expiry:
                return expired;
            case KnowledgeBaseSync.Automatic:
                // A source that cannot tell whether it changed without being read is read again after the delay.
                var stamp = await StampAsync(knowledgeBase, cancellationToken).ConfigureAwait(false);
                return stamp is null ? expired : !string.Equals(stamp, state.ChangeStamp, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private async ValueTask<string?> StampAsync(KnowledgeBaseEntity knowledgeBase, CancellationToken cancellationToken)
    {
        if (!_types.TryGetValue(knowledgeBase.SourceType, out var type))
            return null;
        try
        {
            return await type.ChangeStampAsync(KnowledgeSourceSettings.Of(knowledgeBase.Settings), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "The change stamp of knowledge base {Id} could not be read", knowledgeBase.Id);
            return null;
        }
    }

    private async ValueTask<KnowledgeBaseSyncState> SyncNowAsync(KnowledgeBaseEntity knowledgeBase, CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        var before = await store.CountAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        await store.SaveStateAsync(knowledgeBase.Id, new KnowledgeBaseSyncState(KnowledgeBaseStatus.Syncing, started, null, before.Documents, before.Passages, []), cancellationToken).ConfigureAwait(false);

        if (!_types.TryGetValue(knowledgeBase.SourceType, out var type))
            return await FailAsync(knowledgeBase, started, knowledgeBase.SourceType, $"This installation cannot read sources of type '{knowledgeBase.SourceType}'.", null, cancellationToken).ConfigureAwait(false);

        var settings = KnowledgeSourceSettings.Of(knowledgeBase.Settings);
        string? stamp = null;
        IReadOnlyList<KnowledgeSourceDocument> documents;
        try
        {
            stamp = await type.ChangeStampAsync(settings, cancellationToken).ConfigureAwait(false);
            documents = await type.ReadAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Knowledge base {Id}: the source could not be read", knowledgeBase.Id);
            return await FailAsync(knowledgeBase, started, type.Name, ex.Message, stamp, cancellationToken).ConfigureAwait(false);
        }

        var kept = await store.FingerprintsAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<KnowledgeBaseSyncError>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(document.Text) || !seen.Add(document.Key))
                continue;

            var title = KnowledgeChunker.TitleOf(document.Text, document.Title, Path.GetFileNameWithoutExtension(document.Key) is { Length: > 0 } name ? name : document.Key);
            var fingerprint = Fingerprint(title, document.Text);
            if (kept.TryGetValue(document.Key, out var was) && was == fingerprint)
                continue;

            try
            {
                var chunks = KnowledgeChunker.Split(document.Text, title);
                var vectors = chunks.Count == 0 ? [] : await embedder.EmbedAsync([.. chunks.Select(c => c.Text)], cancellationToken).ConfigureAwait(false);
                var passages = chunks.Select((chunk, i) => new KnowledgeBasePassageRecord(i, chunk.Text, vectors[i], Metadata(document.Metadata, chunk.Section))).ToList();
                await store.WriteDocumentAsync(knowledgeBase.Id, new KnowledgeBaseDocumentRecord(document.Key, fingerprint, title, document.Source, clock.GetUtcNow()), passages, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Knowledge base {Id}: document {Key} could not be kept", knowledgeBase.Id, document.Key);
                errors.Add(new KnowledgeBaseSyncError(document.Source ?? document.Key, ex.Message));
            }
        }

        var gone = kept.Keys.Where(key => !seen.Contains(key)).ToList();
        await store.RemoveDocumentsAsync(knowledgeBase.Id, gone, cancellationToken).ConfigureAwait(false);

        var counts = await store.CountAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        var state = new KnowledgeBaseSyncState(errors.Count == 0 ? KnowledgeBaseStatus.Synced : KnowledgeBaseStatus.Failed, started, clock.GetUtcNow(), counts.Documents, counts.Passages, errors, stamp);
        await store.SaveStateAsync(knowledgeBase.Id, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async ValueTask<KnowledgeBaseSyncState> FailAsync(KnowledgeBaseEntity knowledgeBase, DateTimeOffset started, string source, string message, string? stamp, CancellationToken cancellationToken)
    {
        // What the base held is kept: a source that cannot be read for a while does not empty it.
        var counts = await store.CountAsync(knowledgeBase.Id, cancellationToken).ConfigureAwait(false);
        var state = new KnowledgeBaseSyncState(KnowledgeBaseStatus.Failed, started, clock.GetUtcNow(), counts.Documents, counts.Passages, [new KnowledgeBaseSyncError(source, message)], stamp);
        await store.SaveStateAsync(knowledgeBase.Id, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private static Dictionary<string, string>? Metadata(IReadOnlyDictionary<string, string>? document, string? section)
    {
        var metadata = document is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(document, StringComparer.OrdinalIgnoreCase);
        if (section is not null)
            metadata["section"] = section;
        return metadata.Count == 0 ? null : metadata;
    }

    private static string Fingerprint(string title, string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + text)));
}

/// <summary>Where the last synchronisation of a base stands.</summary>
public static class KnowledgeBaseStatus
{
    public const string Syncing = "syncing";
    public const string Synced = "synced";
    public const string Failed = "failed";
}
