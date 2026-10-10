using System.Text.Json;

namespace Agentivity.Infrastructure.Templates;

/// <summary>A data table as it travels in a Template: its schema asset and, when asked for, its rows.</summary>
/// <param name="Entry">The table asset exactly as the platform stores it.</param>
public sealed record TemplateDataTable(string Id, string Name, JsonElement Entry, IReadOnlyList<JsonElement> Rows);

/// <summary>
/// Gives the Template export access to the shared data tables, which live in an optional module: when the module is not
/// installed nothing is registered, and a Template simply carries no table.
/// </summary>
public interface ITemplateDataTableSource
{
    /// <summary>The table with that id, or null when there is none. Rows are only read when <paramref name="includeRows"/> is true.</summary>
    Task<TemplateDataTable?> GetAsync(string tableId, bool includeRows, CancellationToken cancellationToken = default);
}
