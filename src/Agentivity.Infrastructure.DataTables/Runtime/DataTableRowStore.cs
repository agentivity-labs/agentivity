using System.Collections.Concurrent;
using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.Repository.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentivity.Infrastructure.DataTables.Runtime;

/// <summary>
/// Holds the mutable row data for shared Data Table assets, keyed by <see cref="DataTableEntity.Id"/>
/// — separate from the entity itself (the versioned schema asset). Disk-backed (one JSON file per
/// table, same shape as an AI Agent Session's file persistence), lock-guarded via the underlying
/// <see cref="DataTable"/> domain object.
/// </summary>
public sealed class DataTableRowStore
{
    private readonly ConcurrentDictionary<string, DataTable> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _saveLocks = new(StringComparer.OrdinalIgnoreCase);

    // About 3 s of patience in all (50 + 100 + … + 450 ms), far longer than a scan or an indexer holds a file.
    private const int SaveAttempts = 10;
    private const int SaveRetryDelayMs = 50;
    private readonly IEntityRepository<DataTableEntity> _repository;
    private readonly string _storageRoot;

    private readonly ILogger _logger;

    public DataTableRowStore(IEntityRepository<DataTableEntity> repository, IConfiguration configuration, ILogger<DataTableRowStore>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentNullException.ThrowIfNull(configuration);
        _storageRoot = PathResolver.Resolve(configuration.GetValue<string>("DataTables:RowsPath") ?? "_data/datatables-rows");
    }

    /// <summary>Resolves the live table for the given asset id, loading its schema and persisted rows on first access.</summary>
    public async ValueTask<DataTable> GetOrLoadAsync(string tableId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);

        if (_cache.TryGetValue(tableId, out var cached))
        {
            return cached;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(tableId, out cached))
            {
                return cached;
            }

            var entityResult = await _repository.GetAsync(tableId, cancellationToken).ConfigureAwait(false);
            if (!entityResult.IsSuccess || entityResult.Value is null)
            {
                throw new InvalidOperationException($"Data Table '{tableId}' was not found.");
            }

            var schema = new DataTableSchema(entityResult.Value.Id, entityResult.Value.Columns);
            var table = LoadFromDisk(tableId, schema) ?? new DataTable(schema);
            _cache[tableId] = table;
            return table;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Applies a changed schema (a column added, retyped…) to a table already loaded in memory: the rows stay, under the new schema.
    /// Without this a loaded table keeps the schema it was loaded with until the server restarts — a column added in Studio could
    /// not be written by an agent. Returns null when applied (or when the table is not loaded, so the next load reads the new
    /// schema), otherwise why the change could not be applied — a row that does not fit it — in which case the table keeps working
    /// under its previous schema.
    /// </summary>
    public async ValueTask<string?> RefreshSchemaAsync(string tableId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableId);

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cache.TryGetValue(tableId, out var loaded))
            {
                return null;
            }

            var entityResult = await _repository.GetAsync(tableId, cancellationToken).ConfigureAwait(false);
            if (!entityResult.IsSuccess || entityResult.Value is null)
            {
                _cache.TryRemove(tableId, out _);
                return null;
            }

            try
            {
                _cache[tableId] = loaded.WithSchema(new DataTableSchema(entityResult.Value.Id, entityResult.Value.Columns));
                return null;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Data Table '{TableId}': the new schema could not be applied to the rows in memory", tableId);
                return ex.Message;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Persists the current in-memory row state for the given table id to disk. Saves of one table are serialized, each
    /// writes the rows as they are when its turn comes, and the file is replaced as a whole (written aside, then moved over)
    /// so a reader never sees a half-written table. Windows lets something else — an antivirus scan, an indexer, an editor —
    /// hold the file open for a moment, which refuses the write; the save waits that out rather than failing the tool call
    /// of an agent (and with it the whole run).
    /// </summary>
    public async ValueTask SaveAsync(string tableId, CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(tableId, out var table))
        {
            return;
        }

        var filePath = GetFilePath(tableId);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var gate = _saveLocks.GetOrAdd(tableId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(table.ToList());
            var tempPath = filePath + ".tmp";
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await File.WriteAllBytesAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
                    File.Move(tempPath, filePath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < SaveAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(SaveRetryDelayMs * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < SaveAttempts)
                {
                    // Windows reports a file held open by another process as "access denied" in some cases.
                    await Task.Delay(TimeSpan.FromMilliseconds(SaveRetryDelayMs * attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private DataTable? LoadFromDisk(string tableId, DataTableSchema schema)
    {
        var filePath = GetFilePath(tableId);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json) ?? [];
            return new DataTable(schema, rows.Select(NormalizeRow));
        }
        catch
        {
            return null;
        }
    }

    // System.Text.Json deserializes untyped `object?` values as JsonElement — coerce them back to
    // plain CLR primitives so DataTableColumn.CoerceValue/IsValidValue behave the same whether the
    // row came from disk, from the HTTP endpoints layer, or from a freshly-inserted in-memory value.
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

    private string GetFilePath(string tableId) => Path.Combine(_storageRoot, $"{SanitizeSegment(tableId)}.json");

    private static string SanitizeSegment(string value) => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
