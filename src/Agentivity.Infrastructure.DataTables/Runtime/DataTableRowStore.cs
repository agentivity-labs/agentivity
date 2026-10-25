using System.Collections.Concurrent;
using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentivity.Infrastructure.DataTables.Runtime;

/// <summary>
/// Opens the shared Data Tables: each is a real table of the workspace's database (<see cref="IDataTableStore"/>), opened as a
/// proxy (<see cref="LiveDataTable"/>) — nothing of its rows is held here. Its schema is the one of its definition, the versioned
/// asset (<see cref="DataTableEntity"/>): the table is made to fit it each time it is opened — at no cost when it already does —,
/// so that a definition saved by anyone (Studio, the assistant, a template) reaches it.
/// <para>
/// The first time a table is opened, the rows kept for it before (as JSON, see <see cref="ITableRowsStorage"/>) are taken over
/// with their ids and dates, in the same transaction as the table is made: all of them or none. They are left where they were.
/// </para>
/// </summary>
public sealed class DataTableRowStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _openings = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEntityRepository<DataTableEntity> _repository;
    private readonly IDataTableStore _tables;
    private readonly ITableRowsStorage? _keptBefore;
    private readonly ILogger _logger;

    /// <param name="keptBefore">Where the rows were kept before the tables were real ones: taken over from there, once.</param>
    public DataTableRowStore(IEntityRepository<DataTableEntity> repository, IDataTableStore tables, ITableRowsStorage? keptBefore = null, ILogger<DataTableRowStore>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _keptBefore = keptBefore;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The table of that id, made to fit its definition. Throws <see cref="DataTableUnavailableException"/> when there is no such
    /// table, or when its rows — those it has, or those kept before — do not fit its definition (nothing is then changed).
    /// </summary>
    public async ValueTask<LiveDataTable> OpenAsync(string tableId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        var entity = await _repository.GetAsync(tableId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess || entity.Value is null)
            throw DataTableUnavailableException.NotFound(tableId);

        var schema = new DataTableSchema(entity.Value.Id, entity.Value.Columns);
        // One opening of a table at a time here: the rows kept before are read once, not by each caller.
        var gate = _openings.GetOrAdd(schema.TableName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rowsWhenMade = await _tables.ExistsAsync(schema.TableName, cancellationToken).ConfigureAwait(false) ? null : await KeptBeforeAsync(entity.Value, cancellationToken).ConfigureAwait(false);
            await _tables.ApplySchemaAsync(schema, rowsWhenMade, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex is not DataTableUnavailableException)
        {
            _logger.LogError(ex, "Data Table '{TableId}' cannot be opened: its rows do not fit its definition", tableId);
            throw DataTableUnavailableException.DoNotFit(entity.Value.Name, ex.Message, _tables.Describe(tableId));
        }
        finally
        {
            gate.Release();
        }

        return new LiveDataTable(_tables, schema);
    }

    /// <summary>
    /// Makes the table fit its definition as it is now saved (a column added, retyped…). Returns null when done — or when there is
    /// no such table —, otherwise why the rows do not fit it, in which case the table is left as it was.
    /// </summary>
    public async ValueTask<string?> RefreshSchemaAsync(string tableId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        try
        {
            await OpenAsync(tableId, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DataTableUnavailableException ex)
        {
            return ex.Code == DataTableUnavailableException.NotFoundCode ? null : ex.Why ?? ex.Message;
        }
    }

    /// <summary>
    /// Why the rows of the table would not fit the schema <paramref name="columns"/> — a row with no value for a required column, a
    /// value that cannot be converted —, or null when they all fit (or the table has not been made). Checked before a definition is
    /// saved: nothing is changed.
    /// </summary>
    public async ValueTask<string?> WhyRowsDoNotFitAsync(string tableId, IReadOnlyList<DataTableColumn> columns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        try
        {
            return await _tables.WhyRowsDoNotFitAsync(new DataTableSchema(tableId, columns), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Why restoring an earlier definition, of columns <paramref name="columns"/>, would lose what people put in the table — a column
    /// it does not have that holds values in some rows, or rows that do not fit its types —, or null when it loses nothing. A column
    /// whose name differs only by its case is the same column. Nothing is changed: checked before the version is restored.
    /// </summary>
    public async ValueTask<string?> WhyRestoreLosesValuesAsync(string tableId, IReadOnlyList<DataTableColumn> columns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        ArgumentNullException.ThrowIfNull(columns);
        var entity = await _repository.GetAsync(tableId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess || entity.Value is null)
            return null;

        var current = new DataTableSchema(entity.Value.Id, entity.Value.Columns);
        var lost = new List<string>();
        foreach (var column in current.Columns.Where(c => !columns.Any(v => string.Equals(v.Name, c.Name, StringComparison.OrdinalIgnoreCase))))
        {
            var count = await _tables.CountValuesAsync(current, column.Name, cancellationToken).ConfigureAwait(false);
            if (count > 0)
                lost.Add($"'{column.Name}' (a value in {count} row(s))");
        }

        if (lost.Count > 0)
            return $"This version does not have the column(s) {string.Join(", ", lost)}: restoring it would remove them with their values. Nothing was changed. Copy those values elsewhere, or remove the column(s) yourself first, then restore it.";

        return await WhyRowsDoNotFitAsync(tableId, columns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The table is deleted: its rows go with it, and those kept before for it too (a table made again under its id starts empty).</summary>
    public async ValueTask DeleteAsync(string tableId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);
        await _tables.DropAsync(tableId, cancellationToken).ConfigureAwait(false);
        if (_keptBefore is not null)
            await _keptBefore.DeleteAsync(tableId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DataTableStoredRow>?> KeptBeforeAsync(DataTableEntity entity, CancellationToken cancellationToken)
    {
        if (_keptBefore is null)
            return null;

        try
        {
            var kept = await _keptBefore.LoadAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            return kept?.Select(row => row with { Data = NormalizeRow(new Dictionary<string, object?>(row.Data, StringComparer.OrdinalIgnoreCase)) }).ToList();
        }
        catch (TableRowsUnreadableException ex)
        {
            _logger.LogError(ex, "The rows kept before for Data Table '{TableId}' cannot be read from {Where}", entity.Id, _keptBefore.Describe(entity.Id));
            throw DataTableUnavailableException.Unreadable(entity.Name, ex.Message, _keptBefore.Describe(entity.Id));
        }
    }

    // System.Text.Json deserializes untyped `object?` values as JsonElement — coerce them back to
    // plain CLR primitives so DataTableColumn.CoerceValue/IsValidValue behave the same whether the
    // row came from where it is kept, from the HTTP endpoints layer, or from a tool call.
    // Public: reused by DataTablesEndpoints when binding row insert/update request bodies.
    public static Dictionary<string, object?> NormalizeRow(Dictionary<string, object?> row)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in row)
        {
            result[key] = value is JsonElement element ? NormalizeJsonElement(element) : value;
        }
        return result;
    }

    private static object? NormalizeJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => NormalizeJsonElement(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => null,
        };
}
