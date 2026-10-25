using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Arguments;
using Agentivity.Domain.DataTables;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The Data Tables as real tables of a database, the same way whatever the database (see <see cref="IDataTableStore"/>): what
/// differs from one database to another — how it is opened, the SQL types, the column of the ids, the lock of a table, how JSON is
/// given — is said by the module that keeps the workspace there.
/// <para>
/// Each table is named after its id (<c>dt_</c> and a hash of it: any id gives a plain name), and what the table was made for —
/// its columns and what each holds — is kept in <c>datatable_schemas</c>, so that a change of schema is a change of the table.
/// A column holds one of four things: text, a number, yes/no, or JSON (lists, structured values, and anything else).
/// </para>
/// </summary>
public abstract class DatabaseDataTableStore : IDataTableStore
{
    private const string IdColumn = "id";
    private const string CreatedColumn = "createdAt";
    private const string UpdatedColumn = "updatedAt";
    private static readonly string[] SystemColumns = [IdColumn, CreatedColumn, UpdatedColumn];

    /// <summary>What a column holds, in the database.</summary>
    protected enum Holds
    {
        Text,
        Number,
        YesNo,
        Json,
    }

    /// <summary>A connection to the database, its tables made (<c>datatable_schemas</c> among them).</summary>
    protected abstract Task<DbConnection> OpenAsync(CancellationToken cancellationToken);

    /// <summary>The table of that plain name, as SQL names it (quoted, in its schema if the database has one for them).</summary>
    protected abstract string TableName(string physicalName);

    /// <summary>The SQL type of a column that holds <paramref name="holds"/>.</summary>
    protected abstract string SqlType(Holds holds);

    /// <summary>The definition of the column of the ids: a whole number, the key, given in order and never given twice.</summary>
    protected abstract string IdColumnDefinition { get; }

    public abstract string Describe(string tableId);

    public abstract string SqlDialect { get; }

    /// <summary>How the values of a table are read in <see cref="SqlDialect"/>: yes/no, lists and structured values, dates.</summary>
    protected abstract string QueryNotes { get; }

    public string DescribeForQuery(DataTableSchema schema, string tableName) =>
        $"{Quote(tableName)}({string.Join(", ", new[] { $"{Quote(IdColumn)} {SqlType(Holds.Number)}", $"{Quote(CreatedColumn)} {SqlType(Holds.Text)}", $"{Quote(UpdatedColumn)} {SqlType(Holds.Text)}" }.Concat(schema.Columns.Select(c => $"{Quote(c.Name)} {SqlType(HoldsOf(c))}")))}). {QueryNotes}";

    /// <summary>What an agent is told when its query does more than read its table.</summary>
    protected static string OnlyReads(string tableName) =>
        $"This tool can only read the table {Quote(tableName)}, with one SELECT: no change to the table, no PRAGMA or SET, no other table, file or setting.";

    /// <summary>What an agent is told when its query runs longer than it may.</summary>
    protected static string TooLong(TimeSpan timeout) =>
        $"The query ran too long (more than {timeout.TotalSeconds:0.#} s) and was stopped. Make it simpler: filter with WHERE, group with GROUP BY, add a LIMIT.";

    // The statements that are not a query: refused at once, saying so. Any other first word goes to the database, which says what is
    // wrong with it (a misspelt SELECT is a syntax error the agent corrects) — and which alone decides what a query may read.
    private static readonly HashSet<string> NotAQuery = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "REPLACE", "TRUNCATE", "DROP", "CREATE", "ALTER", "RENAME", "GRANT", "REVOKE",
        "PRAGMA", "ATTACH", "DETACH", "VACUUM", "REINDEX", "ANALYZE", "ANALYSE", "SET", "RESET", "SHOW", "COPY", "DO", "CALL",
        "EXECUTE", "PREPARE", "DEALLOCATE", "DISCARD", "LOAD", "LISTEN", "NOTIFY", "UNLISTEN", "LOCK", "BEGIN", "START", "COMMIT",
        "END", "ROLLBACK", "SAVEPOINT", "RELEASE", "CHECKPOINT", "CLUSTER", "COMMENT", "SECURITY", "IMPORT", "REFRESH", "EXPLAIN",
    };

    /// <summary>
    /// The query without the semicolons it ends with — unless it is plainly not a query (a change, a setting, a PRAGMA…), which is
    /// refused before the database is asked anything (<see cref="DataTableQueryException"/>). The database is still the one that
    /// refuses what a query may not read or do: this only says early what can never be an answer.
    /// </summary>
    protected static string OneSelect(string sql, string tableName)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new DataTableQueryException($"The query is empty: write one SELECT on {Quote(tableName)}.");

        var text = sql.Trim().TrimEnd(';').TrimEnd();
        var start = text.TrimStart('(', ' ', '\t', '\r', '\n');
        // Comments before the first word are left out to read it.
        while (start.StartsWith("--", StringComparison.Ordinal) || start.StartsWith("/*", StringComparison.Ordinal))
        {
            var end = start.StartsWith("--", StringComparison.Ordinal) ? start.IndexOf('\n') : start.IndexOf("*/", StringComparison.Ordinal);
            start = end < 0 ? string.Empty : start[(end + (start.StartsWith("--", StringComparison.Ordinal) ? 1 : 2))..].TrimStart('(', ' ', '\t', '\r', '\n');
        }

        var first = new string(start.TakeWhile(char.IsLetter).ToArray());
        if (NotAQuery.Contains(first))
            throw new DataTableQueryException(OnlyReads(tableName));
        return text;
    }

    /// <summary>The answer of a query: its columns, at most <paramref name="maxRows"/> rows, and whether it gave more.</summary>
    protected static async Task<(List<string> Columns, List<Dictionary<string, object?>> Rows, bool More)> ReadAnswerAsync(DbDataReader reader, int? maxRows, CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<Dictionary<string, object?>>();
        var more = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (maxRows is { } max && rows.Count >= max)
            {
                more = true;
                break;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return (columns, rows, more);
    }

    public abstract ValueTask<DataTableQueryResult> QueryAsync(DataTableSchema schema, string tableName, string sql, int? maxRows, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>After the table <paramref name="tableId"/> was dropped: what the database kept for it besides is let go.</summary>
    protected virtual Task AfterDropAsync(DbConnection connection, DbTransaction transaction, string tableId) => Task.CompletedTask;

    /// <summary>Makes sure no one else changes the table <paramref name="tableId"/> until the transaction ends.</summary>
    protected virtual Task LockAsync(DbConnection connection, DbTransaction transaction, string tableId) => Task.CompletedTask;

    /// <summary>After rows were written with their own ids: the next id given is after the highest.</summary>
    protected virtual Task AfterKeptIdsAsync(DbConnection connection, DbTransaction transaction, string physicalName) => Task.CompletedTask;

    /// <summary>A number as the database is given it — exactly, where it keeps numbers exactly.</summary>
    protected virtual object Number(double value) => value;

    /// <summary>Gives a JSON value (or null) to a parameter.</summary>
    protected virtual void SetJson(DbParameter parameter, string? json) => parameter.Value = (object?)json ?? DBNull.Value;

    /// <summary>The text <paramref name="sql"/> in capitals, every letter (accents included): two texts that differ only by case are then equal.</summary>
    protected abstract string Fold(string sql);

    /// <summary>The text <paramref name="sql"/> compared and sorted character by character, whatever the language of the database.</summary>
    protected virtual string Ordinal(string sql) => sql;

    /// <summary>Where the text <paramref name="needle"/> is first found in <paramref name="haystack"/>, from 1; 0 when it is not.</summary>
    protected abstract string Position(string haystack, string needle);

    /// <summary>The text <paramref name="sql"/> without the spaces, tabs and line ends at its start and end.</summary>
    protected abstract string Trim(string sql);

    /// <summary>A JSON value as text: a text without its quotes, anything else as JSON.</summary>
    protected abstract string JsonText(string sql);

    /// <summary>The items of the JSON list <paramref name="sql"/>, as a table of one column, <c>value</c>, holding each as text.</summary>
    protected abstract string ListItems(string sql);

    /// <summary>How many items the JSON list <paramref name="sql"/> has.</summary>
    protected abstract string ListLength(string sql);

    /// <summary>The plain name, in the database, of the table of <paramref name="tableId"/>.</summary>
    public static string PhysicalName(string tableId) => "dt_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tableId))).ToLowerInvariant()[..24];

    /// <summary>A name as SQL reads it: between double quotes, a double quote in it doubled. Whatever it is, it stays a name.</summary>
    protected static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static Holds HoldsOf(DataTableColumn column) => column.Type switch
    {
        ArgumentType.Text => Holds.Text,
        ArgumentType.Number => Holds.Number,
        ArgumentType.Boolean => Holds.YesNo,
        _ => Holds.Json,
    };

    // ── The schema ───────────────────────────────────────────────────────

    public async ValueTask ApplySchemaAsync(DataTableSchema schema, IReadOnlyList<DataTableStoredRow>? rowsWhenMade = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // A table that already fits its schema — what opening a table finds nearly every time — is only read, never locked.
        if (await AppliedAsync(connection, null, schema.TableName, cancellationToken).ConfigureAwait(false) == Describe(schema))
            return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var made = await ApplyAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);
        if (made && rowsWhenMade is { Count: > 0 })
            await InsertKeptAsync(connection, transaction, schema, rowsWhenMade, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string?> WhyRowsDoNotFitAsync(DataTableSchema schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The change is made, to see; and never kept.
            await ApplyAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Whether the table was made by this call.
    private async Task<bool> ApplyAsync(DbConnection connection, DbTransaction transaction, DataTableSchema schema, CancellationToken cancellationToken)
    {
        var named = schema.Columns.FirstOrDefault(c => SystemColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
        if (named is not null)
            throw new InvalidOperationException($"A column cannot be named '{named.Name}': every row already has its {string.Join(", ", SystemColumns)}.");

        var tableId = schema.TableName;
        await LockAsync(connection, transaction, tableId).ConfigureAwait(false);
        var physical = PhysicalName(tableId);
        var table = TableName(physical);
        var applied = await AppliedColumnsAsync(connection, transaction, tableId, cancellationToken).ConfigureAwait(false);
        if (applied is null)
        {
            var columns = new[] { $"{Quote(IdColumn)} {IdColumnDefinition}", $"{Quote(CreatedColumn)} {SqlType(Holds.Text)} NOT NULL", $"{Quote(UpdatedColumn)} {SqlType(Holds.Text)} NOT NULL" }
                .Concat(schema.Columns.Select(c => $"{Quote(c.Name)} {SqlType(HoldsOf(c))}"));
            await ExecuteAsync(connection, transaction, $"CREATE TABLE {table} ({string.Join(", ", columns)})", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "INSERT INTO datatable_schemas (table_id, physical_name, columns) VALUES (@id, @physical, @columns)", cancellationToken, ("id", tableId), ("physical", physical), ("columns", Describe(schema))).ConfigureAwait(false);
            return true;
        }

        // Removed, then renamed and converted, then added: each name is free when it is given.
        foreach (var gone in applied.Where(a => schema.GetColumn(a.Name) is null))
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} DROP COLUMN {Quote(gone.Name)}", cancellationToken).ConfigureAwait(false);

        foreach (var column in schema.Columns)
        {
            var before = applied.FirstOrDefault(a => string.Equals(a.Name, column.Name, StringComparison.OrdinalIgnoreCase));
            if (before is null)
            {
                await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {Quote(column.Name)} {SqlType(HoldsOf(column))}", cancellationToken).ConfigureAwait(false);
                if (column.Default is not null)
                    await SetEveryRowAsync(connection, transaction, table, column, cancellationToken).ConfigureAwait(false);
            }
            else if (before.Holds != HoldsOf(column))
            {
                await ConvertAsync(connection, transaction, table, before, column, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(before.Name, column.Name, StringComparison.Ordinal))
            {
                await RenameAsync(connection, transaction, table, before.Name, column.Name, cancellationToken).ConfigureAwait(false);
            }

            if (column.Required && column.Default is null)
                await NoRowWithoutAsync(connection, transaction, table, column, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, "UPDATE datatable_schemas SET columns = @columns WHERE table_id = @id", cancellationToken, ("id", tableId), ("columns", Describe(schema))).ConfigureAwait(false);
        return false;
    }

    // What a column was made for: its name, what it holds, whether it is required, and its default (as JSON).
    private sealed record AppliedColumn(string Name, Holds Holds, bool Required = false, string? Default = null);

    private static string Describe(DataTableSchema schema) =>
        JsonSerializer.Serialize(schema.Columns.Select(c => new AppliedColumn(c.Name, HoldsOf(c), c.Required, c.Default is null ? null : JsonSerializer.Serialize(c.Default))));

    private static async Task<string?> AppliedAsync(DbConnection connection, DbTransaction? transaction, string tableId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT columns FROM datatable_schemas WHERE table_id = @id", ("id", tableId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<List<AppliedColumn>?> AppliedColumnsAsync(DbConnection connection, DbTransaction? transaction, string tableId, CancellationToken cancellationToken) =>
        await AppliedAsync(connection, transaction, tableId, cancellationToken).ConfigureAwait(false) is { } columns ? JsonSerializer.Deserialize<List<AppliedColumn>>(columns) : null;

    private async Task SetEveryRowAsync(DbConnection connection, DbTransaction transaction, string table, DataTableColumn column, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"UPDATE {table} SET {Quote(column.Name)} = @value");
        AddValue(command, "value", column, column.CoerceValue(column.Default));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task NoRowWithoutAsync(DbConnection connection, DbTransaction transaction, string table, DataTableColumn column, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT MIN({Quote(IdColumn)}), COUNT(*) FROM {table} WHERE {Quote(column.Name)} IS NULL");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        if (count > 0)
            throw new DataTableRowsDoNotFitException($"Row {Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)} has no value for the column '{column.Name}', which cannot be required then ({count} row(s) have none). Give them one, or give the column a default.");
    }

    // A name that differs only by its case is the same name to some databases: it goes through another one.
    private async Task RenameAsync(DbConnection connection, DbTransaction transaction, string table, string from, string to, CancellationToken cancellationToken)
    {
        var passing = "__renaming_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} RENAME COLUMN {Quote(from)} TO {Quote(passing)}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} RENAME COLUMN {Quote(passing)} TO {Quote(to)}", cancellationToken).ConfigureAwait(false);
    }

    // A column whose type changes: its values converted by the schema, a page at a time, into a column of the new type, which then
    // takes its place. A value that cannot be converted stops it all, naming its row.
    private async Task ConvertAsync(DbConnection connection, DbTransaction transaction, string table, AppliedColumn before, DataTableColumn column, CancellationToken cancellationToken)
    {
        var converted = "__converting_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {Quote(converted)} {SqlType(HoldsOf(column))}", cancellationToken).ConfigureAwait(false);
        var oldColumn = new DataTableColumn(before.Name, before.Holds switch { Holds.Text => ArgumentType.Text, Holds.Number => ArgumentType.Number, Holds.YesNo => ArgumentType.Boolean, _ => ArgumentType.Structured });
        var newColumn = new DataTableColumn(converted, column.Type, column.Required, column.Default);
        long after = 0;
        while (true)
        {
            var page = new List<(long Id, object? Value)>();
            await using (var read = Command(connection, transaction, $"SELECT {Quote(IdColumn)}, {Quote(before.Name)} FROM {table} WHERE {Quote(IdColumn)} > @after ORDER BY {Quote(IdColumn)} LIMIT {IDataTableStore.MaxPageSize}", ("after", after)))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    page.Add((reader.GetInt64(0), FromDatabase(oldColumn, reader.IsDBNull(1) ? null : reader.GetValue(1))));
            }

            if (page.Count == 0)
                break;

            foreach (var (id, value) in page)
            {
                var given = value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value;
                if (given is not null && !column.IsValidValue(given))
                    throw new DataTableRowsDoNotFitException($"The column '{column.Name}' cannot become {column.Type}: the value of row {id} ({given}) is not one.");
                await using var write = Command(connection, transaction, $"UPDATE {table} SET {Quote(converted)} = @value WHERE {Quote(IdColumn)} = @id", ("id", id));
                AddValue(write, "value", newColumn, given is null ? null : column.CoerceValue(given));
                await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            after = page[^1].Id;
        }

        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} DROP COLUMN {Quote(before.Name)}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} RENAME COLUMN {Quote(converted)} TO {Quote(column.Name)}", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long> CountValuesAsync(DataTableSchema schema, string column, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var named = DataTableSearchPlan.ColumnNamed(schema, column);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await AppliedAsync(connection, null, schema.TableName, cancellationToken).ConfigureAwait(false) is null)
            return 0;

        await using var command = Command(connection, null, $"SELECT COUNT({Quote(named)}) FROM {TableName(PhysicalName(schema.TableName))}");
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async ValueTask<bool> ExistsAsync(string tableId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await AppliedColumnsAsync(connection, null, tableId, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async ValueTask DropAsync(string tableId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, tableId).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"DROP TABLE IF EXISTS {TableName(PhysicalName(tableId))}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM datatable_schemas WHERE table_id = @id", cancellationToken, ("id", tableId)).ConfigureAwait(false);
        await AfterDropAsync(connection, transaction, tableId).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── The rows ─────────────────────────────────────────────────────────

    public async ValueTask<DataTableRow> InsertAsync(DataTableSchema schema, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        var errors = schema.Validate(values);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Validation failed: {string.Join("; ", errors)}");

        var data = schema.CoerceValues(values);
        var now = DateTime.UtcNow;
        var columns = schema.Columns.Where(c => data.ContainsKey(c.Name)).ToList();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"INSERT INTO {TableName(PhysicalName(schema.TableName))} ({string.Join(", ", new[] { CreatedColumn, UpdatedColumn }.Concat(columns.Select(c => c.Name)).Select(Quote))}) VALUES (@created, @updated{string.Concat(columns.Select((_, i) => $", @v{i}"))}) RETURNING {Quote(IdColumn)}", ("created", Date(now)), ("updated", Date(now)));
        for (var i = 0; i < columns.Count; i++)
            AddValue(command, $"v{i}", columns[i], data[columns[i].Name]);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return DataTableRow.Kept(id, now, now, data);
    }

    public async ValueTask InsertKeptAsync(DataTableSchema schema, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertKeptAsync(connection, transaction, schema, rows, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertKeptAsync(DbConnection connection, DbTransaction transaction, DataTableSchema schema, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken)
    {
        var physical = PhysicalName(schema.TableName);
        var columns = schema.Columns.ToList();
        var perStatement = RowsPerStatement(columns.Count + 3);
        // One statement for every full group of rows, given new values each time; one more for the rows left at the end.
        DbCommand? command = null;
        try
        {
            foreach (var chunk in rows.Chunk(perStatement))
            {
                if (command is null || command.Parameters.Count != chunk.Length * (columns.Count + 3))
                {
                    if (command is not null)
                        await command.DisposeAsync().ConfigureAwait(false);
                    command = KeptRowsInsert(connection, transaction, TableName(physical), columns, chunk.Length);
                }

                var at = 0;
                foreach (var row in chunk)
                {
                    var errors = schema.Validate(row.Data);
                    if (errors.Count > 0)
                        throw new InvalidOperationException($"Row {row.Id} does not fit the schema: {string.Join("; ", errors)}");
                    var data = schema.CoerceValues(row.Data);
                    command.Parameters[at++].Value = row.Id;
                    command.Parameters[at++].Value = Date(row.CreatedAt);
                    command.Parameters[at++].Value = Date(row.UpdatedAt);
                    foreach (var column in columns)
                        SetValue(command.Parameters[at++], column, data.TryGetValue(column.Name, out var value) ? value : null);
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (command is not null)
                await command.DisposeAsync().ConfigureAwait(false);
        }

        await AfterKeptIdsAsync(connection, transaction, physical).ConfigureAwait(false);
    }

    /// <summary>
    /// How many rows one statement writes, when rows are written many at once, for <paramref name="parametersPerRow"/> values each:
    /// as many as the parameters allow (some databases take no more than 32 766).
    /// </summary>
    protected virtual int RowsPerStatement(int parametersPerRow) => Math.Max(1, Math.Min(500, 30_000 / parametersPerRow));

    private DbCommand KeptRowsInsert(DbConnection connection, DbTransaction transaction, string table, IReadOnlyList<DataTableColumn> columns, int rows)
    {
        var command = Command(connection, transaction, string.Empty);
        var tuples = new List<string>(rows);
        for (var r = 0; r < rows; r++)
        {
            Add(command, $"i{r}", null);
            Add(command, $"c{r}", null);
            Add(command, $"u{r}", null);
            for (var c = 0; c < columns.Count; c++)
                AddValue(command, $"v{r}_{c}", columns[c], null);
            tuples.Add($"(@i{r}, @c{r}, @u{r}{string.Concat(columns.Select((_, c) => $", @v{r}_{c}"))})");
        }

        command.CommandText = $"INSERT INTO {table} ({string.Join(", ", new[] { IdColumn, CreatedColumn, UpdatedColumn }.Concat(columns.Select(c => c.Name)).Select(Quote))}) VALUES {string.Join(", ", tuples)}";
        return command;
    }

    public async ValueTask<DataTableRow?> FindAsync(DataTableSchema schema, long id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(connection, null, schema, id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DataTableRow?> FindAsync(DbConnection connection, DbTransaction? transaction, DataTableSchema schema, long id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Selected(schema)} FROM {TableName(PhysicalName(schema.TableName))} WHERE {Quote(IdColumn)} = @id", ("id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader, schema) : null;
    }

    public async ValueTask<DataTableRow?> UpdateAsync(DataTableSchema schema, long id, IReadOnlyDictionary<string, object?> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(updates);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await FindAsync(connection, transaction, schema, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return null;

        var merged = new Dictionary<string, object?>(current.Data, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in updates)
            merged[key] = value;
        var errors = schema.Validate(merged);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Validation failed: {string.Join("; ", errors)}");

        // Only the columns it names: the others keep their values, whatever their defaults.
        var changed = updates.Keys.Select(schema.GetColumn).OfType<DataTableColumn>().ToList();
        var now = DateTime.UtcNow;
        await using (var command = Command(connection, transaction, $"UPDATE {TableName(PhysicalName(schema.TableName))} SET {Quote(UpdatedColumn)} = @updated{string.Concat(changed.Select((c, i) => $", {Quote(c.Name)} = @v{i}"))} WHERE {Quote(IdColumn)} = @id", ("updated", Date(now)), ("id", id)))
        {
            for (var i = 0; i < changed.Count; i++)
                AddValue(command, $"v{i}", changed[i], changed[i].CoerceValue(merged[changed[i].Name]));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var updated = await FindAsync(connection, transaction, schema, id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async ValueTask<bool> DeleteRowAsync(string tableId, long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"DELETE FROM {TableName(PhysicalName(tableId))} WHERE {Quote(IdColumn)} = @id", ("id", id));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<DataTablePage> PageAsync(DataTableSchema schema, int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var take = Math.Clamp(limit, 0, IDataTableStore.MaxPageSize);
        var table = TableName(PhysicalName(schema.TableName));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<DataTableRow>();
        await using (var command = Command(connection, null, $"SELECT {Selected(schema)} FROM {table} ORDER BY {Quote(IdColumn)} LIMIT @take OFFSET @skip", ("take", (long)take), ("skip", (long)Math.Max(0, offset))))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(Read(reader, schema));
        }

        await using var count = Command(connection, null, $"SELECT COUNT(*) FROM {table}");
        return new DataTablePage(rows, Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture));
    }

    public async ValueTask<DataTableSearchResult> SearchAsync(DataTableSchema schema, DataTableQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(query);
        // Checked before the database is asked anything: what it finds, and why it cannot be done, are the same everywhere.
        var plan = DataTableSearchPlan.Of(schema, query);
        var parameters = new List<object>();
        var where = string.Join(" AND ", plan.Where.Select(f => Sql(schema, f, parameters)).DefaultIfEmpty("1 = 1"));
        var table = TableName(PhysicalName(schema.TableName));
        var take = Math.Min(plan.Limit ?? IDataTableStore.MaxPageSize, IDataTableStore.MaxPageSize);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<DataTableRow>();
        if (take > 0)
        {
            var order = string.Concat(plan.OrderBy.Select(s => SortSql(schema, s))) + $"{Quote(IdColumn)}";
            await using var command = Command(connection, null, $"SELECT {Selected(schema)} FROM {table} WHERE {where} ORDER BY {order} LIMIT {take} OFFSET {plan.Offset}");
            AddAll(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(Read(reader, schema));
        }

        await using var count = Command(connection, null, $"SELECT COUNT(*) FROM {table} WHERE {where}");
        AddAll(count, parameters);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new DataTableSearchResult(rows, total, plan.Offset);
    }

    private static void AddAll(DbCommand command, List<object> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
            Add(command, $"q{i}", parameters[i]);
    }

    // A condition or a group, as SQL: the values are parameters (q0, q1…), the columns names the table has.
    private string Sql(DataTableSchema schema, DataTablePlannedFilter filter, List<object> parameters)
    {
        if (filter is DataTablePlannedGroup group)
            return group.Items.Count == 0 ? "1 = 1" : "(" + string.Join(group.AnyOf ? " OR " : " AND ", group.Items.Select(i => Sql(schema, i, parameters))) + ")";

        var (column, kind, op, value, value2) = (DataTablePlannedCondition)filter;
        string Parameter(object given)
        {
            parameters.Add(given switch { DateTime date => Date(date), double number => Number(number), _ => given });
            return $"@q{parameters.Count - 1}";
        }

        var stored = Quote(column);
        if (kind == DataTableValueKind.List)
        {
            var empty = $"({stored} IS NULL OR {ListLength(stored)} = 0)";
            return op switch
            {
                DataTableOperator.IsEmpty => empty,
                DataTableOperator.IsNotEmpty => $"NOT {empty}",
                _ => $"EXISTS (SELECT 1 FROM {ListItems(stored)} WHERE {Fold("value")} = {Fold(Parameter(value!))})",
            };
        }

        // What is compared: texts in capitals, character by character; dates (kept as ISO 8601 text) character by character.
        var isText = kind == DataTableValueKind.Text;
        var asText = isText && schema.GetColumn(column) is { } own && HoldsOf(own) == Holds.Json ? JsonText(stored) : stored;
        string Compared(string sql) => isText ? Ordinal(Fold(sql)) : kind == DataTableValueKind.Date ? Ordinal(sql) : sql;
        var left = Compared(asText);
        switch (op)
        {
            case DataTableOperator.IsEmpty:
                return isText ? $"({asText} IS NULL OR {Trim(asText)} = '')" : $"{asText} IS NULL";
            case DataTableOperator.IsNotEmpty:
                return isText ? $"({asText} IS NOT NULL AND {Trim(asText)} <> '')" : $"{asText} IS NOT NULL";
            case DataTableOperator.Between:
                return $"({left} >= {Compared(Parameter(value!))} AND {left} <= {Compared(Parameter(value2!))})";
            case DataTableOperator.Contains:
                return $"{Position(Fold(asText), Fold(Parameter(value!)))} > 0";
            case DataTableOperator.StartsWith:
                return $"{Position(Fold(asText), Fold(Parameter(value!)))} = 1";
            case DataTableOperator.NotEquals:
                return $"({asText} IS NULL OR {left} <> {Compared(Parameter(value!))})";
        }

        var comparison = op switch
        {
            DataTableOperator.Equals => "=",
            DataTableOperator.GreaterThan => ">",
            DataTableOperator.GreaterOrEqual => ">=",
            DataTableOperator.LessThan => "<",
            _ => "<=",
        };
        return $"{left} {comparison} {Compared(Parameter(value!))}";
    }

    // Empty values last, whichever way the column is sorted.
    private string SortSql(DataTableSchema schema, DataTablePlannedSort sort)
    {
        var stored = Quote(sort.Column);
        var key = sort.Kind switch
        {
            DataTableValueKind.Text => Ordinal(Fold(schema.GetColumn(sort.Column) is { } own && HoldsOf(own) == Holds.Json ? JsonText(stored) : stored)),
            DataTableValueKind.List => Ordinal(Fold(JsonText(stored))),
            DataTableValueKind.Date => Ordinal(stored),
            _ => stored,
        };
        return $"CASE WHEN {stored} IS NULL THEN 1 ELSE 0 END, {key} {(sort.Descending ? "DESC" : "ASC")}, ";
    }

    // ── Values, in and out ───────────────────────────────────────────────

    private static string Selected(DataTableSchema schema) => string.Join(", ", new[] { IdColumn, CreatedColumn, UpdatedColumn }.Concat(schema.Columns.Select(c => c.Name)).Select(Quote));

    private static DataTableRow Read(DbDataReader reader, DataTableSchema schema)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var columns = schema.Columns;
        // A column with no value is not in the row (as when the rows were kept as JSON): only what is there is read.
        for (var i = 0; i < columns.Count; i++)
        {
            if (!reader.IsDBNull(i + 3))
                data[columns[i].Name] = FromDatabase(columns[i], reader.GetValue(i + 3));
        }
        return DataTableRow.Kept(reader.GetInt64(0), ParseDate(reader.GetString(1)), ParseDate(reader.GetString(2)), data);
    }

    private static object? FromDatabase(DataTableColumn column, object? raw)
    {
        if (raw is null)
            return null;

        return HoldsOf(column) switch
        {
            Holds.Text => Convert.ToString(raw, CultureInfo.InvariantCulture),
            Holds.Number => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
            Holds.YesNo => raw is bool yes ? yes : Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0,
            _ => FromJson(column, Convert.ToString(raw, CultureInfo.InvariantCulture)!),
        };
    }

    private static object? FromJson(DataTableColumn column, string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = Plain(document.RootElement);
        return column.Type == ArgumentType.TextList && value is List<object?> items ? items.Select(i => i?.ToString() ?? string.Empty).ToList() : value;
    }

    // JSON as plain values: text, whole numbers, numbers, yes/no, lists, and names with values.
    private static object? Plain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var whole) => whole,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value), StringComparer.OrdinalIgnoreCase),
        _ => null,
    };

    private void AddValue(DbCommand command, string name, DataTableColumn column, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        SetValue(parameter, column, value);
        command.Parameters.Add(parameter);
    }

    private void SetValue(DbParameter parameter, DataTableColumn column, object? value)
    {
        switch (HoldsOf(column))
        {
            case Holds.Json:
                SetJson(parameter, value is null ? null : JsonSerializer.Serialize(value));
                break;
            case Holds.Text:
                parameter.Value = value is null ? DBNull.Value : Convert.ToString(value, CultureInfo.InvariantCulture)!;
                break;
            case Holds.Number:
                parameter.Value = value is null ? DBNull.Value : Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                parameter.Value = value is null ? DBNull.Value : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static string Date(DateTime value) => DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // ── Commands ─────────────────────────────────────────────────────────

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            Add(command, name, value);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
