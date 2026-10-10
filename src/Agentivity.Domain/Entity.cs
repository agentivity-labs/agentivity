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

    /// <summary>Tags libres pour la recherche et le filtrage dans la bibliothèque.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Date de première création.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Date de dernière modification.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
