namespace Agentivity.Infrastructure.Templates;

/// <summary>
/// Lets the Template import create the shared data tables (schema asset and rows), which live in an optional module. When the
/// module is not installed nothing is registered and a Template that carries a table cannot be imported.
/// </summary>
public interface ITemplateDataTableSink
{
    /// <summary>Creates the table described by <paramref name="table"/> (its <c>Id</c> is the new one) and numbers its rows afresh. Returns an error message, or null when it worked.</summary>
    Task<string?> CreateAsync(TemplateDataTable table, CancellationToken cancellationToken = default);

    /// <summary>Removes a table created by <see cref="CreateAsync"/> (used to undo a half-done import).</summary>
    Task DeleteAsync(string tableId, CancellationToken cancellationToken = default);
}
