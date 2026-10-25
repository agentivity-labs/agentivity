using System.Text.Json;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.VectorStores;

/// <summary>
/// In-Memory Vector Store — texts and their vectors kept for one run only, free, with no account: a memory key names the
/// collection. The collection lives in the run's own state, so no other run ever sees it, and a run that pauses (waiting for a
/// person) still has it when it resumes. Its vectors are made with the Embeddings connected to it, or the free local model.
/// </summary>
public sealed class InMemoryVectorStoreNode : ResourceNode, IVectorStoreProvider
{
    private const string StatePrefix = "__vectorstore.inmemory:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public InMemoryVectorStoreNode()
        : base("ai.vectorstore.inmemory")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.VectorStore);

        DefineMetadata(
            "In-Memory Vector Store",
            "Texts kept with their vectors for the time of one run, and found again by meaning; free, with no account.",
            "ai.vectorstore",
            whenToUse: "To keep texts while a run lasts and find them again by meaning, for free: what a step inserts, a later step or an agent of the same run finds. Nothing is kept after the run: to keep texts from one run to the next, use Qdrant, Pinecone or pgvector. For what an agent should know (documents, pages, feeds), use a Knowledge Base instead."
        );
        DefineEffects(NodeEffects.None);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Vector Store", ResourcePortCategory.VectorStore, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "The model that makes the vectors (optional; the free Local Embeddings model when nothing is connected).", isRequired: false);

        DefineInput("memory_key", ArgumentType.Text, "Names the collection: the steps of a run that use the same key share the same texts.", "default", ui: new ArgumentUiMetadata { Group = "Collection", Order = 10 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public Task UpsertAsync(ResourceBinding binding, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        var (collection, key) = OpenCopy(binding);
        foreach (var record in records)
        {
            Check(collection, key, record.Vector.Length);
            collection.Records.RemoveAll(r => r.Id == record.Id);
            collection.Records.Add(new Entry(record.Id, record.Text, record.Vector, record.Metadata is null ? null : new Dictionary<string, string>(record.Metadata)));
        }

        Save(binding, key, collection);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] query, int limit, IReadOnlyDictionary<string, string>? filter = null, CancellationToken cancellationToken = default)
    {
        var (collection, key) = Open(binding);
        if (collection.Records.Count > 0)
            Check(collection, key, query.Length);
        IReadOnlyList<VectorMatch> found =
        [
            .. collection.Records
                .Where(r => filter is null || filter.All(f => r.Metadata is not null && r.Metadata.TryGetValue(f.Key, out var value) && string.Equals(value, f.Value, StringComparison.Ordinal)))
                .Select(r => new VectorMatch(r.Id, r.Text, Cosine(query, r.Vector), r.Metadata))
                .OrderByDescending(m => m.Score)
                .Take(limit),
        ];
        return Task.FromResult(found);
    }

    public Task DeleteAsync(ResourceBinding binding, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        var (collection, key) = OpenCopy(binding);
        collection.Records.RemoveAll(r => ids.Contains(r.Id));
        Save(binding, key, collection);
        return Task.CompletedTask;
    }

    public Task ClearAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var (_, key) = Open(binding);
        Save(binding, key, new Collection());
        return Task.CompletedTask;
    }

    // ── The collection, in the run's state ───────────────────────────────

    /// <summary>The texts of one memory key; plain data, so the run's state can be saved and read back (pause, resume).</summary>
    public sealed class Collection
    {
        public List<Entry> Records { get; set; } = [];
    }

    public sealed record Entry(string Id, string Text, float[] Vector, Dictionary<string, string>? Metadata);

    private static (Collection Collection, string Key) Open(ResourceBinding binding)
    {
        var state = binding.State ?? throw new InvalidOperationException("The In-Memory Vector Store keeps its texts for one run only, and it was used outside of a run.");
        var key = StatePrefix + (binding.GetString("memory_key")?.Trim() is { Length: > 0 } named ? named : "default");
        var collection = state.TryGetValue(key, out var kept)
            ? kept switch
            {
                Collection c => c,
                JsonElement saved => saved.Deserialize<Collection>(Json) ?? new Collection(),
                _ => new Collection(),
            }
            : new Collection();
        return (collection, key);
    }

    /// <summary>A copy to change, saved in place of the one kept: the run may be saved (a pause) while a tool writes.</summary>
    private static (Collection Collection, string Key) OpenCopy(ResourceBinding binding)
    {
        var (kept, key) = Open(binding);
        return (new Collection { Records = [.. kept.Records] }, key);
    }

    private static void Save(ResourceBinding binding, string key, Collection collection) => binding.State![key] = collection;

    private static void Check(Collection collection, string key, int size)
    {
        if (collection.Records.FirstOrDefault() is { } first && first.Vector.Length != size)
            throw new VectorSizeMismatchException($"The In-Memory collection '{key[StatePrefix.Length..]}'", first.Vector.Length, size);
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : (float)(dot / Math.Sqrt(na * nb));
    }
}
