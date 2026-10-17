namespace Agentivity.Domain.DataTables;

/// <summary>
/// A row of a Data Table as it is kept: its id, the date it was made, the date it was last changed, and its values. A table
/// read back from where it is kept is made of these rows as they are (<see cref="DataTable.Restore"/>), not of new ones.
/// </summary>
public sealed record DataTableStoredRow(long Id, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyDictionary<string, object?> Data);
