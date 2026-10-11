using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Capabilities;

/// <summary>
/// What the platform can do, as discovered from the subsystems that are loaded. The AI assistant, the validator and the
/// Studio read this; none of them carries a catalog of its own.
/// </summary>
public interface ICapabilityRegistry
{
    /// <summary>The current picture. Built on first use, then kept until <see cref="Refresh"/>.</summary>
    CapabilitySnapshot Current { get; }

    /// <summary>Asks every contributor again and replaces <see cref="Current"/>. Raises <see cref="Changed"/> when the content differs.</summary>
    CapabilitySnapshot Refresh();

    /// <summary>Raised after a refresh produced a different version.</summary>
    event Action<CapabilitySnapshot>? Changed;

    /// <summary>
    /// Finds capabilities for a need written in plain words. Uses words and meaning together when the meaning index is ready,
    /// words alone otherwise (the result says which). <paramref name="lexicalOnly"/> forces words alone, to compare.
    /// </summary>
    Task<CapabilitySearchResult> SearchAsync(string query, string? kind = null, int limit = 10, bool lexicalOnly = false, CancellationToken cancellationToken = default);

    /// <summary>Builds (or completes) the meaning index of the current capabilities. Safe to call again; cheap when nothing changed.</summary>
    Task<SemanticStatus> PrepareSemanticAsync(CancellationToken cancellationToken = default);

    /// <summary>Where the meaning index stands, without building anything.</summary>
    SemanticStatus Semantic { get; }
}

/// <param name="Mode"><c>hybrid</c> (words and meaning) or <c>lexical</c> (words only).</param>
/// <param name="Note">Why the search was words only, when it was.</param>
public sealed record CapabilitySearchResult(string Mode, IReadOnlyList<CapabilityHit> Hits, string? Note = null);

/// <param name="Ready">True when meaning searches use the index of the current capabilities.</param>
/// <param name="Vectors">How many capabilities have a vector.</param>
/// <param name="Computed">How many vectors the last build had to compute (the rest came from the cache).</param>
/// <param name="Reason">Why it is not ready, when it is not.</param>
public sealed record SemanticStatus(bool Ready, int Vectors, int Computed, string? Model, string? Reason);

/// <summary>
/// An immutable picture of every capability at one moment, with a version that changes whenever the content does.
/// </summary>
public sealed class CapabilitySnapshot
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '"', '\'', '-', '_', '+', '=', '*', '?', '!', '<', '>', '|', '#', '&'];

    // Words that carry no meaning for finding a capability; left in, they match everything equally.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "or", "of", "to", "by", "in", "on", "for", "with", "that", "this", "is", "are", "it", "you", "your", "can", "then", "from", "as", "at", "be", "an", "do", "me", "my", "we", "how", "what",
    };

    private readonly Dictionary<(string Kind, string Id), CapabilityDescriptor> _byKey;
    private readonly IndexedDocument[] _documents;
    private readonly Dictionary<string, int> _documentFrequency;

    internal CapabilitySnapshot(IReadOnlyList<CapabilityDescriptor> items, IReadOnlyList<string> problems)
    {
        Items = items;
        Problems = problems;
        _byKey = items.ToDictionary(i => (i.Kind.ToLowerInvariant(), i.Id.ToLowerInvariant()));
        Version = ComputeVersion(items);

        _documents = [.. items.Select(Index)];
        _documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in _documents)
        foreach (var term in document.Weights.Keys)
            _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
    }

    /// <summary>A short fingerprint of the content: equal versions mean equal capabilities.</summary>
    public string Version { get; }

    /// <summary>Every capability, ordered by kind then id.</summary>
    public IReadOnlyList<CapabilityDescriptor> Items { get; }

    /// <summary>Contributors that failed or entries that were refused, so a broken subsystem is visible instead of silent.</summary>
    public IReadOnlyList<string> Problems { get; }

    public CapabilityDescriptor? Get(string kind, string id) => _byKey.GetValueOrDefault((kind.ToLowerInvariant(), id.ToLowerInvariant()));

    public IReadOnlyList<CapabilityDescriptor> List(string? kind = null, string? category = null) =>
        [.. Items.Where(i => (kind is null || string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase)) && (category is null || string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase)))];

    public IReadOnlyList<CapabilityKindOverview> Overview() =>
        [
            .. Items
                .GroupBy(i => i.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CapabilityKindOverview(
                    g.Key,
                    g.Count(),
                    [.. g.Where(i => !string.IsNullOrWhiteSpace(i.Category)).GroupBy(i => i.Category!, StringComparer.OrdinalIgnoreCase).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => new CapabilityCategoryOverview(c.Key, c.Count()))]
                )),
        ];

    /// <summary>
    /// Lexical search over names, ids, categories, descriptions, "when to use" and keywords. A term that begins like a
    /// query word counts a little; an exact term counts fully; rarer terms count more.
    /// </summary>
    public IReadOnlyList<CapabilityHit> Search(string query, string? kind = null, int limit = 10)
    {
        var terms = Tokenize(query).Distinct().ToArray();
        if (terms.Length == 0 || limit <= 0)
            return [];

        var total = Math.Max(1, _documents.Length);
        var hits = new List<CapabilityHit>();
        foreach (var document in _documents)
        {
            if (kind is not null && !string.Equals(document.Item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                continue;

            double score = 0;
            var matchedTerms = 0;
            foreach (var term in terms)
            {
                var best = 0.0;
                if (document.Weights.TryGetValue(term, out var exact))
                {
                    var idf = Math.Log(1 + (double)total / (1 + _documentFrequency.GetValueOrDefault(term)));
                    best = exact * idf;
                }
                else if (term.Length >= 3)
                {
                    foreach (var (candidate, weight) in document.Weights)
                    {
                        // The same word in another form (investigate / investigation, scrap / scraper): a long common beginning.
                        var shared = CommonPrefix(term, candidate);
                        var shortest = Math.Min(term.Length, candidate.Length);
                        if (shared >= 3 && (shared == term.Length || shared >= Math.Max(5, (int)Math.Ceiling(shortest * 0.7))))
                            best = Math.Max(best, weight * 0.4);
                    }
                }

                if (best > 0)
                    matchedTerms++;
                score += best;
            }

            if (score <= 0)
                continue;

            // Matching every word of the query matters more than matching one of them very well.
            score *= 0.5 + 0.5 * matchedTerms / terms.Length;
            hits.Add(new CapabilityHit(document.Item, Math.Round(score, 3)));
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Capability.Kind, StringComparer.Ordinal).ThenBy(h => h.Capability.Id, StringComparer.Ordinal).Take(limit)];
    }

    /// <summary>The first sentence of the description, capped, for lists.</summary>
    public static string Summary(CapabilityDescriptor item)
    {
        var text = item.Description.Trim();
        if (text.Length == 0)
            return "";
        var end = text.IndexOfAny(['.', '\n']);
        var sentence = end > 0 ? text[..(end + 1)] : text;
        return sentence.Length <= 180 ? sentence : sentence[..177].TrimEnd() + "...";
    }

    /// <summary>The text a model reads for this capability: the owner's card, or a generic one.</summary>
    public static string CardOf(CapabilityDescriptor item)
    {
        if (!string.IsNullOrWhiteSpace(item.Card))
            return item.Card!;
        var sb = new StringBuilder();
        sb.Append(item.Name).Append(" [").Append(item.Kind).Append(':').Append(item.Id).Append(']');
        if (!string.IsNullOrWhiteSpace(item.Category))
            sb.Append(" — ").Append(item.Category);
        sb.AppendLine();
        if (item.Description.Length > 0)
            sb.AppendLine(item.Description);
        if (!string.IsNullOrWhiteSpace(item.WhenToUse))
            sb.Append("Use when: ").AppendLine(item.WhenToUse);
        return sb.ToString().TrimEnd();
    }

    internal static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length < 2 || StopWords.Contains(token))
                continue;
            if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
                token = token[..^1];
            yield return token;
        }
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
            i++;
        return i;
    }

    private static IndexedDocument Index(CapabilityDescriptor item)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);

        void Add(string? text, double weight)
        {
            foreach (var token in Tokenize(text))
                weights[token] = Math.Max(weights.GetValueOrDefault(token), weight);
        }

        Add(item.Id, 3);
        Add(item.Name, 3);
        foreach (var keyword in item.Keywords)
            Add(keyword, 2.5);
        Add(item.WhenToUse, 2);
        Add(item.Description, 2);
        Add(item.Category, 1);
        return new IndexedDocument(item, weights);
    }

    private static string ComputeVersion(IReadOnlyList<CapabilityDescriptor> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items)
        {
            sb.Append(i.Kind).Append('\u001f').Append(i.Id).Append('\u001f').Append(i.Name).Append('\u001f').Append(i.Description).Append('\u001f').Append(i.WhenToUse).Append('\u001f').Append(i.Category).Append('\u001f');
            sb.Append(string.Join('\u001e', i.Keywords)).Append('\u001f').Append(i.Details.ToJsonString(new JsonSerializerOptions { WriteIndented = false })).Append('\u001f').Append(i.Card).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    private sealed record IndexedDocument(CapabilityDescriptor Item, Dictionary<string, double> Weights);
}

/// <summary>
/// Aggregates what every <see cref="ICapabilityContributor"/> declares. A contributor that throws is recorded and skipped:
/// one broken module must not hide the rest of the platform from the assistant.
/// </summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private const int Candidates = 40;
    private const double RrfK = 60;

    private readonly IReadOnlyCollection<ICapabilityContributor> _contributors;
    private readonly ILogger<CapabilityRegistry>? _logger;
    private readonly ICapabilityEmbedder? _embedder;
    private readonly ICapabilityEmbeddingCache? _cache;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _building = new(1, 1);
    private readonly Dictionary<string, float[]> _queryVectors = new(StringComparer.OrdinalIgnoreCase);
    private CapabilitySnapshot? _current;
    private CapabilitySemanticIndex? _semantic;
    private string? _semanticVersion;
    private string? _semanticReason = "The meaning index has not been built yet.";

    public CapabilityRegistry(IEnumerable<ICapabilityContributor> contributors, ILogger<CapabilityRegistry>? logger = null, ICapabilityEmbedder? embedder = null, ICapabilityEmbeddingCache? cache = null)
    {
        _contributors = [.. contributors ?? throw new ArgumentNullException(nameof(contributors))];
        _logger = logger;
        _embedder = embedder;
        _cache = cache;
    }

    /// <summary>A meaning match below this similarity is noise and is left out.</summary>
    public double SemanticFloor { get; set; } = 0.25;

    public event Action<CapabilitySnapshot>? Changed;

    public CapabilitySnapshot Current
    {
        get
        {
            lock (_gate)
                return _current ??= Build();
        }
    }

    public CapabilitySnapshot Refresh()
    {
        CapabilitySnapshot next;
        bool changed;
        lock (_gate)
        {
            var previous = _current;
            next = Build();
            _current = next;
            changed = previous is not null && previous.Version != next.Version;
            if (changed)
            {
                _semantic = null;
                _semanticVersion = null;
                _semanticReason = "The capabilities changed; the meaning index is being rebuilt.";
            }
        }

        if (changed)
            Changed?.Invoke(next);
        return next;
    }

    public SemanticStatus Semantic
    {
        get
        {
            lock (_gate)
            {
                var ready = _semantic is not null && _current is not null && _semanticVersion == _current.Version;
                return new SemanticStatus(ready, ready ? _semantic!.Count : 0, ready ? _semantic!.Computed : 0, _embedder?.ModelId, ready ? null : _semanticReason);
            }
        }
    }

    public async Task<SemanticStatus> PrepareSemanticAsync(CancellationToken cancellationToken = default)
    {
        if (_embedder is null)
        {
            lock (_gate)
                _semanticReason = "No embedding model is configured.";
            return Semantic;
        }

        await _building.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Current;
            lock (_gate)
            {
                if (_semantic is not null && _semanticVersion == snapshot.Version)
                    return Semantic;
            }

            if (!await _embedder.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_gate)
                    _semanticReason = $"The embedding model '{_embedder.ModelId}' is not available.";
                return Semantic;
            }

            try
            {
                var index = await CapabilitySemanticIndex.BuildAsync(snapshot, _embedder, _cache, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    // Only kept if the capabilities did not change while it was being built.
                    if (_current is not null && _current.Version == snapshot.Version)
                    {
                        _semantic = index;
                        _semanticVersion = snapshot.Version;
                        _semanticReason = null;
                    }
                }

                _logger?.LogInformation("Capability meaning index ready: {Vectors} vectors, {Computed} computed, model {Model}", index.Count, index.Computed, _embedder.ModelId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_gate)
                    _semanticReason = "The meaning index could not be built: " + ex.Message;
                _logger?.LogWarning(ex, "Capability meaning index could not be built");
            }

            return Semantic;
        }
        finally
        {
            _building.Release();
        }
    }

    public async Task<CapabilitySearchResult> SearchAsync(string query, string? kind = null, int limit = 10, bool lexicalOnly = false, CancellationToken cancellationToken = default)
    {
        var snapshot = Current;
        var lexical = snapshot.Search(query, kind, Candidates);
        if (lexicalOnly)
            return new CapabilitySearchResult("lexical", [.. lexical.Take(limit)], "Words only, as asked.");
        if (string.IsNullOrWhiteSpace(query))
            return new CapabilitySearchResult("lexical", []);

        CapabilitySemanticIndex? index;
        lock (_gate)
            index = _semantic is not null && _semanticVersion == snapshot.Version ? _semantic : null;

        if (index is null)
        {
            // Not ready: answer with words now, and get the index ready for the next search.
            if (_embedder is not null)
                _ = Task.Run(() => PrepareSemanticAsync(CancellationToken.None), CancellationToken.None);
            return new CapabilitySearchResult("lexical", [.. lexical.Take(limit)], Semantic.Reason);
        }

        float[] vector;
        try
        {
            vector = await QueryVectorAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Capability search fell back to words: the query could not be embedded");
            return new CapabilitySearchResult("lexical", [.. lexical.Take(limit)], "The query could not be embedded: " + ex.Message);
        }

        var semantic = index.Search(vector, kind, Candidates, SemanticFloor);
        return new CapabilitySearchResult("hybrid", Fuse(lexical, semantic, limit));
    }

    private async Task<float[]> QueryVectorAsync(string query, CancellationToken ct)
    {
        var key = query.Trim();
        lock (_queryVectors)
        {
            if (_queryVectors.TryGetValue(key, out var cached))
                return cached;
        }

        var vector = (await _embedder!.EmbedAsync([key], ct).ConfigureAwait(false))[0];
        lock (_queryVectors)
        {
            if (_queryVectors.Count > 256)
                _queryVectors.Clear();
            _queryVectors[key] = vector;
        }

        return vector;
    }

    /// <summary>
    /// Reciprocal rank fusion: a capability is as high as its rank in the words list and in the meaning list together. It
    /// needs no weights to tune, and a capability both lists agree on rises above one only a single list likes.
    /// </summary>
    private static IReadOnlyList<CapabilityHit> Fuse(IReadOnlyList<CapabilityHit> lexical, IReadOnlyList<CapabilityHit> semantic, int limit)
    {
        var scores = new Dictionary<string, (CapabilityDescriptor Item, double Score, bool Words, bool Meaning)>();

        void Add(IReadOnlyList<CapabilityHit> list, bool words)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var item = list[rank].Capability;
                var key = CapabilitySemanticIndex.KeyOf(item);
                var gain = 1.0 / (RrfK + rank + 1);
                scores.TryGetValue(key, out var current);
                scores[key] = (item, current.Score + gain, current.Words || words, current.Meaning || !words);
            }
        }

        Add(lexical, words: true);
        Add(semantic, words: false);

        var best = 2.0 / (RrfK + 1);
        return
        [
            .. scores
                .Values.OrderByDescending(v => v.Score)
                .ThenBy(v => v.Item.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(v => new CapabilityHit(v.Item, Math.Round(v.Score / best, 3), v.Words && v.Meaning ? "both" : v.Words ? "words" : "meaning")),
        ];
    }

    private CapabilitySnapshot Build()
    {
        var items = new Dictionary<(string, string), CapabilityDescriptor>();
        var problems = new List<string>();

        foreach (var contributor in _contributors)
        {
            IReadOnlyCollection<CapabilityDescriptor> declared;
            try
            {
                declared = contributor.GetCapabilities();
            }
            catch (Exception ex)
            {
                var message = $"{contributor.GetType().Name} failed: {ex.Message}";
                problems.Add(message);
                _logger?.LogWarning(ex, "Capability contributor {Contributor} failed and was skipped", contributor.GetType().Name);
                continue;
            }

            foreach (var item in declared)
            {
                if (string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Id))
                {
                    problems.Add($"{contributor.GetType().Name} declared a capability without a kind or an id");
                    continue;
                }

                var key = (item.Kind.ToLowerInvariant(), item.Id.ToLowerInvariant());
                if (items.ContainsKey(key))
                    problems.Add($"{item.Kind}:{item.Id} is declared more than once; the last declaration wins");
                items[key] = item;
            }
        }

        var ordered = items.Values.OrderBy(i => i.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var snapshot = new CapabilitySnapshot(ordered, problems);
        _logger?.LogInformation("Capability registry built: {Count} capabilities, version {Version}, {Problems} problem(s)", ordered.Count, snapshot.Version, problems.Count);
        return snapshot;
    }
}
