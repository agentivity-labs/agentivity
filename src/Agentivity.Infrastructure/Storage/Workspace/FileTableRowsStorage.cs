using System.Globalization;
using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The rows of the Data Tables in files, when the workspace module keeps none itself: one JSON file per table under
/// <c>DataTables:RowsPath</c> (by default <c>_data/datatables-rows</c>), each row with its <c>id</c>, <c>createdAt</c>,
/// <c>updatedAt</c> and its values. A file is replaced as a whole (written aside, then moved over), so a reader never sees a
/// half-written table. Windows lets something else — an antivirus scan, an indexer, an editor — hold the file open for a moment,
/// which refuses the write; the save waits that out rather than failing the tool call of an agent (and with it the whole run).
/// </summary>
public sealed class FileTableRowsStorage : ITableRowsStorage
{
    // About 3 s of patience in all (50 + 100 + … + 450 ms), far longer than a scan or an indexer holds a file.
    private const int SaveAttempts = 10;
    private const int SaveRetryDelayMs = 50;
    private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase) { "id", "createdAt", "updatedAt" };

    private readonly string _root;
    private readonly ILogger _logger;

    public FileTableRowsStorage(IConfiguration configuration, ILogger<FileTableRowsStorage>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _root = PathResolver.Resolve(configuration.GetValue<string>("DataTables:RowsPath") ?? "_data/datatables-rows");
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string Describe(string tableId) => FilePath(tableId);

    /// <summary>The folder the rows are kept in.</summary>
    public string Location => _root;

    /// <summary>The tables whose rows are kept here, by id: what a workspace module that keeps the rows itself takes over.</summary>
    public IReadOnlyList<string> KeptTables() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal).ToArray() : [];

    public async ValueTask<IReadOnlyList<DataTableStoredRow>?> LoadAsync(string tableId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(tableId);
        if (!File.Exists(path))
            return null;

        List<Dictionary<string, JsonElement>>? raw;
        try
        {
            await using var stream = File.OpenRead(path);
            raw = await JsonSerializer.DeserializeAsync<List<Dictionary<string, JsonElement>>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new TableRowsUnreadableException("their file is not valid JSON.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TableRowsUnreadableException($"their file cannot be opened: {ex.Message}", ex);
        }

        var rows = new List<DataTableStoredRow>();
        // A row kept without an id (by a version that did not keep it) gets the next one: it is not lost.
        long next = (raw ?? []).Select(r => r.TryGetValue("id", out var id) && id.TryGetInt64(out var value) ? value : 0).DefaultIfEmpty(0).Max() + 1;
        foreach (var row in raw ?? [])
        {
            var id = row.TryGetValue("id", out var idValue) && idValue.TryGetInt64(out var kept) ? kept : next++;
            var created = DateOf(row, "createdAt") ?? DateTime.UtcNow;
            var updated = DateOf(row, "updatedAt") ?? created;
            rows.Add(new DataTableStoredRow(id, created, updated, row.Where(p => !SystemColumns.Contains(p.Key)).ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase)));
        }

        return rows;
    }

    public async ValueTask SaveAsync(string tableId, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken = default)
    {
        var path = FilePath(tableId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(rows.Select(row =>
        {
            var kept = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = row.Id, ["createdAt"] = row.CreatedAt, ["updatedAt"] = row.UpdatedAt };
            foreach (var (key, value) in row.Data)
                kept[key] = value;
            return kept;
        }).ToList());

        var tempPath = path + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, path, overwrite: true);
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

    public ValueTask DeleteAsync(string tableId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(tableId);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("The rows of Data Table {TableId} were deleted with it ({Path})", tableId, path);
        }

        return ValueTask.CompletedTask;
    }

    private static DateTime? DateOf(Dictionary<string, JsonElement> row, string key) =>
        row.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    private string FilePath(string tableId) => Path.Combine(_root, $"{string.Concat(tableId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))}.json");
}
