using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentivity.Infrastructure.Capabilities;

/// <summary>
/// Turns text into vectors so that things that mean the same are close, whatever words they use. The registry asks this for
/// the meaning of capabilities and of a search; when it is absent or fails, search falls back to words alone.
/// </summary>
public interface ICapabilityEmbedder
{
    /// <summary>Names the model: a cache of vectors is only valid for the model that made them.</summary>
    string ModelId { get; }

    /// <summary>False while the model cannot be used (not downloaded, not loaded). Search then uses words only. Never loads anything.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the model ready if it is not (this may download it) and says whether it can be used. Called from the background,
    /// never from a request. A failure is remembered: it is not retried on every call.
    /// </summary>
    Task<bool> EnsureAvailableAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>Keeps the vectors of capabilities between runs, so that a start-up only computes what changed.</summary>
public interface ICapabilityEmbeddingCache
{
    IReadOnlyDictionary<string, CachedVector> Load(string modelId);

    void Save(string modelId, IReadOnlyDictionary<string, CachedVector> vectors);
}

/// <param name="Hash">A fingerprint of the text the vector was made from: a changed text means a new vector.</param>
public sealed record CachedVector(string Hash, float[] Vector);

/// <summary>The text of a capability that its meaning is read from: name, what it does, when to use it, its family.</summary>
public static class CapabilityEmbeddingText
{
    public static string Of(CapabilityDescriptor item)
    {
        var sb = new StringBuilder();
        sb.Append(item.Name).Append('.');
        if (item.Description.Length > 0)
            sb.Append(' ').Append(item.Description.Trim());
        if (!string.IsNullOrWhiteSpace(item.WhenToUse))
            sb.Append(" Use when: ").Append(item.WhenToUse.Trim());
        if (!string.IsNullOrWhiteSpace(item.Category))
            sb.Append(" Family: ").Append(item.Category).Append('.');
        return sb.ToString();
    }

    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}

/// <summary>The vectors of every capability of one snapshot, ready to be searched.</summary>
public sealed class CapabilitySemanticIndex
{
    private readonly Dictionary<string, float[]> _vectors;
    private readonly CapabilitySnapshot _snapshot;

    private CapabilitySemanticIndex(CapabilitySnapshot snapshot, Dictionary<string, float[]> vectors, int computed)
    {
        _snapshot = snapshot;
        _vectors = vectors;
        Computed = computed;
    }

    /// <summary>How many vectors had to be computed to build this index (the others came from the cache).</summary>
    public int Computed { get; }

    public int Count => _vectors.Count;

    public static string KeyOf(CapabilityDescriptor item) => item.Kind.ToLowerInvariant() + "\u001f" + item.Id.ToLowerInvariant();

    public static async Task<CapabilitySemanticIndex> BuildAsync(CapabilitySnapshot snapshot, ICapabilityEmbedder embedder, ICapabilityEmbeddingCache? cache, CancellationToken cancellationToken = default)
    {
        var known = cache?.Load(embedder.ModelId) ?? new Dictionary<string, CachedVector>();
        var vectors = new Dictionary<string, float[]>();
        var toCompute = new List<(CapabilityDescriptor Item, string Text, string Hash)>();

        foreach (var item in snapshot.Items)
        {
            // Nothing is said about it: a vector of its name alone is a vector of noise, and it would surface for any query.
            // It stays findable by its identifier (words), and the fix is to describe it.
            if (string.IsNullOrWhiteSpace(item.Description))
                continue;

            var text = CapabilityEmbeddingText.Of(item);
            var hash = CapabilityEmbeddingText.HashOf(text);
            var key = KeyOf(item);
            if (known.TryGetValue(key, out var cached) && cached.Hash == hash)
                vectors[key] = cached.Vector;
            else
                toCompute.Add((item, text, hash));
        }

        var fresh = new Dictionary<string, CachedVector>();
        // In batches: a model has a limit on what it takes at once, and a failure should lose one batch, not everything.
        foreach (var batch in toCompute.Chunk(32))
        {
            var computed = await embedder.EmbedAsync([.. batch.Select(b => b.Text)], cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Length; i++)
            {
                var vector = Normalize(computed[i]);
                var key = KeyOf(batch[i].Item);
                vectors[key] = vector;
                fresh[key] = new CachedVector(batch[i].Hash, vector);
            }
        }

        if (cache is not null && fresh.Count > 0)
        {
            // What is kept is what is current: capabilities that are gone drop out of the cache.
            var keep = new Dictionary<string, CachedVector>();
            foreach (var item in snapshot.Items)
            {
                var key = KeyOf(item);
                if (fresh.TryGetValue(key, out var f))
                    keep[key] = f;
                else if (known.TryGetValue(key, out var k))
                    keep[key] = k;
            }

            cache.Save(embedder.ModelId, keep);
        }

        return new CapabilitySemanticIndex(snapshot, vectors, fresh.Count);
    }

    /// <summary>The capabilities closest in meaning to <paramref name="query"/> (an already computed vector), best first.</summary>
    public IReadOnlyList<CapabilityHit> Search(float[] query, string? kind, int limit, double floor = 0.2)
    {
        var q = Normalize(query);
        var hits = new List<CapabilityHit>();
        foreach (var item in _snapshot.Items)
        {
            if (kind is not null && !string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_vectors.TryGetValue(KeyOf(item), out var vector))
                continue;
            var cosine = Dot(q, vector);
            if (cosine >= floor)
                hits.Add(new CapabilityHit(item, Math.Round(cosine, 4)));
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Capability.Id, StringComparer.Ordinal).Take(limit)];
    }

    public static float[] Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v)
            norm += (double)x * x;
        norm = Math.Sqrt(norm);
        if (norm < 1e-12)
            return v;
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] / norm);
        return result;
    }

    private static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < n; i++)
            sum += (double)a[i] * b[i];
        return sum;
    }
}

/// <summary>Vectors kept as one JSON file per model under a cache folder.</summary>
public sealed class FileCapabilityEmbeddingCache(string directory) : ICapabilityEmbeddingCache
{
    private sealed record Entry(string Key, string Hash, string Vector);

    private string PathOf(string modelId) => Path.Combine(directory, new string([.. modelId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]) + ".json");

    public IReadOnlyDictionary<string, CachedVector> Load(string modelId)
    {
        var path = PathOf(modelId);
        if (!File.Exists(path))
            return new Dictionary<string, CachedVector>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
            return entries.ToDictionary(e => e.Key, e => new CachedVector(e.Hash, Decode(e.Vector)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException)
        {
            // A damaged cache is only a cache: it is recomputed.
            return new Dictionary<string, CachedVector>();
        }
    }

    public void Save(string modelId, IReadOnlyDictionary<string, CachedVector> vectors)
    {
        Directory.CreateDirectory(directory);
        var path = PathOf(modelId);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(vectors.Select(p => new Entry(p.Key, p.Value.Hash, Encode(p.Value.Vector)))));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static float[] Decode(string text)
    {
        var bytes = Convert.FromBase64String(text);
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
