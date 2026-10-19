using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentivity.Domain;

namespace Agentivity.Infrastructure.KnowledgeBases.Domain;

/// <summary>
/// A Knowledge Base, the versioned Workspace entity: what it reads (its type of data source and the settings of that source) and
/// how it is kept up to date. What it holds — documents, passages, their vectors, the state of the last synchronisation — is kept
/// by its engine in the workspace's database (<see cref="Agentivity.Infrastructure.Storage.Workspace.IKnowledgeBaseStore"/>),
/// not versioned: the same split as a Data Table's schema and its rows.
/// </summary>
public sealed record KnowledgeBaseEntity : Entity
{
    /// <summary>Object type string this entity is routed under in the Workspace store.</summary>
    public const string ObjectType = "knowledge_base";

    [JsonConstructor]
    public KnowledgeBaseEntity(string id, string name)
        : base(id, name) { }

    /// <summary>The type of data source it reads (files, web pages, sitemap, RSS…): see <see cref="Sources.IKnowledgeSourceType"/>.</summary>
    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = "";

    /// <summary>The settings of its data source, as that type declares them (a folder, addresses, a feed…).</summary>
    [JsonPropertyName("settings")]
    public JsonObject Settings { get; init; } = [];

    [JsonPropertyName("sync")]
    public KnowledgeBaseSync Sync { get; init; } = new();
}

/// <summary>How a base is kept up to date: by hand, after a delay, or when its source changes.</summary>
public sealed record KnowledgeBaseSync
{
    /// <summary>Synchronised only when asked (and at its first search).</summary>
    public const string Manual = "manual";

    /// <summary>Synchronised again at the next search once <see cref="ExpiryHours"/> have passed.</summary>
    public const string Expiry = "expiry";

    /// <summary>Synchronised again at the next search when its source says it changed (a file modified, a new article…).</summary>
    public const string Automatic = "automatic";

    public static readonly IReadOnlyList<string> Modes = [Manual, Expiry, Automatic];

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = Manual;

    [JsonPropertyName("expiryHours")]
    public int ExpiryHours { get; init; } = 24;
}
