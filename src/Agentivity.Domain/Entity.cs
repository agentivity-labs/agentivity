using System.Text.Json.Serialization;

namespace Agentivity.Domain;

public abstract record Entity
{
    protected Entity(string id, string name)
    {
        if (!Guid.TryParse(id, out _))
            throw new ArgumentException($"Entity.Id must be a valid GUID, got: '{id}'.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; init; }

    /// <summary>Rôle de l'entité — guide le LLM pour choisir le prochain agent dans une team.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    private readonly IReadOnlyList<string> _tags = [];

    /// <summary>
    /// Tags libres pour la recherche et le filtrage dans la bibliothèque. Toujours en minuscules, sans espace autour, chacun une
    /// fois : « Solution » et « solution » sont le même tag, quel que soit le chemin par lequel il arrive.
    /// </summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        init => _tags = NormalizeTags(value);
    }

    /// <summary>Les tags tels qu'une entité les garde : en minuscules, sans vide, sans doublon, dans leur ordre.</summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string?>? tags) =>
        tags is null ? [] : [.. tags.Select(t => t?.Trim().ToLowerInvariant()).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).Distinct(StringComparer.Ordinal)];

    /// <summary>Date de première création.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Date de dernière modification.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
