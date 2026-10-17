using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain;
using Agentivity.Infrastructure.Core.Json;

namespace Agentivity.Infrastructure.Repository.Entities;

/// <summary>
/// How an entity is kept as the payload of a workspace object (<see cref="WorkspaceEntityRepository{TEntity}"/>): a wrapper that
/// says its type (<c>kind</c>), its name, and the entity itself as JSON (<c>entryJson</c>). A version of the entity is the same
/// payload, as it was: reading one — before restoring it, say — is reading this.
/// </summary>
public static class EntityPayload
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Default;

    /// <summary>The payload of <paramref name="entity"/>, of type <paramref name="objectType"/>.</summary>
    public static string Write<TEntity>(TEntity entity, string objectType) where TEntity : Entity =>
        JsonSerializer.Serialize(new Wrapper { ObjectType = objectType, Name = entity.Name, EntryJson = JsonSerializer.Serialize(entity, Json) }, Json);

    /// <summary>
    /// The entity a payload holds, when it holds one of type <paramref name="objectType"/>; null otherwise. Throws
    /// <see cref="JsonException"/> when it cannot be read.
    /// </summary>
    public static TEntity? Read<TEntity>(string payload, string objectType) where TEntity : Entity
    {
        var wrapper = JsonSerializer.Deserialize<Wrapper>(payload, Json);
        return wrapper is null || !string.Equals(wrapper.ObjectType, objectType, StringComparison.OrdinalIgnoreCase) ? null : JsonSerializer.Deserialize<TEntity>(wrapper.EntryJson, Json);
    }

    private sealed class Wrapper
    {
        // JsonPropertyName("kind") preserves on-disk compatibility with the pre-existing Agentic
        // envelope shape (AgenticLibraryPayload.Kind) — agent/team data written before this
        // generic repository existed must still deserialize correctly.
        [JsonPropertyName("kind")]
        public string ObjectType { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string EntryJson { get; set; } = string.Empty;
    }
}
