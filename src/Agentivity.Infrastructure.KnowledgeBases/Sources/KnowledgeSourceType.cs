using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;

namespace Agentivity.Infrastructure.KnowledgeBases.Sources;

/// <summary>
/// What every type of data source shares: it reads documents (text and what is known of each), and they are given a key — the
/// same document keeps the same key from one reading to the next, which is how a synchronisation knows what changed — and a
/// title and a source when the reading gives them.
/// </summary>
public abstract class KnowledgeSourceType : IKnowledgeSourceType
{
    public abstract string TypeId { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract int Icon { get; }

    public abstract IReadOnlyList<ArgumentDefinition> Settings { get; }

    public virtual bool ReadsTheServer => false;

    public virtual IReadOnlyList<string> InstallationSettings => [];

    /// <summary>The documents of the source, read now; their metadata says what each is (<c>source</c>, <c>title</c>, <c>url</c>…).</summary>
    protected abstract ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken);

    public virtual ValueTask<string?> ChangeStampAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

    public async ValueTask<IReadOnlyList<KnowledgeSourceDocument>> ReadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var documents = await LoadAsync(settings, cancellationToken).ConfigureAwait(false);
        var read = new List<KnowledgeSourceDocument>(documents.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            var metadata = document.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var source = Value(metadata, "source");
            var key = KeyOf(metadata, source, index);
            // Two documents of one source with the same key (two items of one feed without addresses…) stay apart.
            for (var n = 2; !keys.Add(key); n++)
                key = $"{KeyOf(metadata, source, index)}#{n}";
            read.Add(new KnowledgeSourceDocument(key, document.Text, Value(metadata, "title"), Value(metadata, "item_url") ?? source, metadata));
        }

        return read;
    }

    /// <summary>A setting, the way a step declares an input.</summary>
    protected static ArgumentDefinition Input(string name, ArgumentType type, string? description = null, object? defaultValue = null, ArgumentUiMetadata? ui = null, bool? required = null) =>
        new(name, type, description, defaultValue, null, ArgumentEditorMode.Auto, null, null, ui) { Required = required };

    /// <summary>A setting with a list of choices.</summary>
    protected static ArgumentDefinition Choice(string name, IEnumerable<(string Value, string Label)> choices, string? description = null, string? defaultValue = null, ArgumentUiMetadata? ui = null, bool? required = null) =>
        new(name, ArgumentType.Choice, description, defaultValue, null, ArgumentEditorMode.Fixed, [.. choices.Select(c => new ChoiceOption(c.Value, c.Label))], null, ui) { Required = required };

    /// <summary>A setting that is one of the person's connections of a kind.</summary>
    protected static ArgumentDefinition Credentials(string name, string templateId, string? description = null, ArgumentUiMetadata? ui = null, bool? required = null) =>
        new(name, ArgumentType.Credentials, description, null, null, ArgumentEditorMode.Fixed, null, templateId, ui) { Required = required };

    private static string KeyOf(IReadOnlyDictionary<string, string> metadata, string? source, int index)
    {
        if ((Value(metadata, "key") ?? Value(metadata, "item_url") ?? Value(metadata, "relative_path") ?? Value(metadata, "id")) is { } named)
            return named;
        var position = Value(metadata, "item_index") ?? Value(metadata, "row_index");
        return position is not null ? $"{source ?? "item"}#{position}" : source ?? $"document {index + 1}";
    }

    private static string? Value(IReadOnlyDictionary<string, string> metadata, string name) => metadata.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
