using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Arguments;

namespace Agentivity.Infrastructure.KnowledgeBases.Sources;

/// <summary>A document read from a data source: its key there (a path, an address), its text, its title and where it comes from.</summary>
public sealed record KnowledgeSourceDocument(string Key, string Text, string? Title, string? Source, IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// A type of data source a Knowledge Base can read: files, web pages, a sitemap, a feed, an API, a database… It says what it needs
/// (its settings, shown as a form) and reads every document of the source. The engine of the base does the rest.
/// </summary>
public interface IKnowledgeSourceType
{
    /// <summary>Its identifier, kept in the base (<c>files</c>, <c>web_pages</c>…).</summary>
    string TypeId { get; }

    string Name { get; }

    string Description { get; }

    /// <summary>Its icon in the Studio: a code point of the Material icons, as a connection template gives its own.</summary>
    int Icon { get; }

    /// <summary>True when it reads the server's own files: left out of the hosted edition, where nobody but the platform has files there.</summary>
    bool ReadsTheServer => false;

    /// <summary>
    /// Its settings that name a place of this installation (a path on the server, a connection string): a Template carries a base
    /// without them, and the person who imports it fills them in.
    /// </summary>
    IReadOnlyList<string> InstallationSettings => [];

    /// <summary>What it needs, as the inputs of a step declare them: the form of a base of this type.</summary>
    IReadOnlyList<ArgumentDefinition> Settings { get; }

    /// <summary>Every document of the source, read now. Throws, saying why, when the source cannot be read.</summary>
    ValueTask<IReadOnlyList<KnowledgeSourceDocument>> ReadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// A short stamp that changes when the source changes (the dates of the files, the last article of a feed…), read without
    /// reading the documents; null when the source cannot tell without being read.
    /// </summary>
    ValueTask<string?> ChangeStampAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
}

/// <summary>The settings of a base's data source, read as a source type needs them.</summary>
public sealed class KnowledgeSourceSettings(JsonObject values)
{
    public static KnowledgeSourceSettings Of(JsonObject? values) => new(values ?? []);

    public string? GetString(string name) => values[name] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s,
        JsonValue v => v.ToJsonString(),
        JsonNode n => n.ToJsonString(),
        _ => null,
    };

    public string RequireString(string name) => GetString(name) ?? throw new InvalidOperationException($"The setting '{name}' is required and has no value.");

    public int? GetInt(string name) => values[name] is JsonValue v
        ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        : null;

    public bool? GetBool(string name) => values[name] is JsonValue v
        ? v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) ? parsed : null
        : null;

    public JsonElement? GetElement(string name) => values[name] is { } node ? JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()) : null;
}
