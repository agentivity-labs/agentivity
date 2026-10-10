using System.Text.Json.Serialization;
using Agentivity.Domain;
using Agentivity.Domain.DataTables;

namespace Agentivity.Infrastructure.DataTables.Domain;

/// <summary>
/// The schema of a shared Data Table asset — the versioned Workspace entity (columns only, no
/// rows). Row data lives separately in <see cref="Runtime.DataTableRowStore"/>, not versioned,
/// keyed by this entity's <see cref="Entity.Id"/> — same split as an AI Agent Session's definition
/// (versioned) vs. runtime state (not versioned).
/// </summary>
public sealed record DataTableEntity : Entity
{
    /// <summary>Object type string this entity is routed under in the Workspace store.</summary>
    public const string ObjectType = "datatable";

    [JsonConstructor]
    public DataTableEntity(string id, string name)
        : base(id, name) { }

    [JsonPropertyName("columns")]
    public IReadOnlyList<DataTableColumn> Columns { get; init; } = [];
}
