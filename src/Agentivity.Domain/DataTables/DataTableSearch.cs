using System.Collections;
using System.Globalization;
using System.Text.Json;
using Agentivity.Domain.Arguments;

namespace Agentivity.Domain.DataTables;

/// <summary>What a column holds, as a search compares it.</summary>
public enum DataTableValueKind
{
    Text,
    Number,
    YesNo,
    List,
    Date,
}

/// <summary>A condition, or a group of them, ready to be checked: see <see cref="DataTableSearchPlan"/>.</summary>
public abstract record DataTablePlannedFilter;

/// <summary>
/// A condition ready to be checked: the column as the table spells it (<c>id</c>, <c>createdAt</c>, <c>updatedAt</c> or one of its
/// own), what it holds, the operator (<c>= nothing</c> already made <c>is_empty</c>), and the values converted to what the column
/// holds — a <see cref="double"/>, a <see cref="bool"/>, a <see cref="DateTime"/> (UTC) or a <see cref="string"/>.
/// </summary>
public sealed record DataTablePlannedCondition(string Column, DataTableValueKind Kind, DataTableOperator Operator, object? Value, object? Value2) : DataTablePlannedFilter;

/// <summary>A group ready to be checked: all of its items true, or one at least (<see cref="AnyOf"/>). An empty group is always true.</summary>
public sealed record DataTablePlannedGroup(bool AnyOf, IReadOnlyList<DataTablePlannedFilter> Items) : DataTablePlannedFilter;

/// <summary>A sort ready to be done: empty values last, whichever way the column is sorted; then by id.</summary>
public sealed record DataTablePlannedSort(string Column, DataTableValueKind Kind, bool Descending);

/// <summary>
/// A search checked against the table before any row is read: every column exists, every operator fits its column, every value is
/// converted to what the column holds — whatever way it was written (a number sent as text, "true" for a yes/no column, a date
/// printed as text). A search that cannot be done says why at once (<see cref="DataTableQueryException"/>), in words the person —
/// or the agent — can correct it with. The same plan is read in memory and turned into SQL: a search finds the same rows wherever
/// the table is kept.
/// </summary>
public sealed record DataTableSearchPlan(IReadOnlyList<DataTablePlannedFilter> Where, IReadOnlyList<DataTablePlannedSort> OrderBy, int? Limit, int Offset)
{
    public static readonly string[] SystemColumns = ["id", "createdAt", "updatedAt"];

    private static readonly Dictionary<DataTableValueKind, DataTableOperator[]> Allowed = new()
    {
        [DataTableValueKind.Text] = Enum.GetValues<DataTableOperator>(),
        [DataTableValueKind.Number] = [DataTableOperator.Equals, DataTableOperator.NotEquals, DataTableOperator.GreaterThan, DataTableOperator.GreaterOrEqual, DataTableOperator.LessThan, DataTableOperator.LessOrEqual, DataTableOperator.Between, DataTableOperator.IsEmpty, DataTableOperator.IsNotEmpty],
        [DataTableValueKind.Date] = [DataTableOperator.Equals, DataTableOperator.NotEquals, DataTableOperator.GreaterThan, DataTableOperator.GreaterOrEqual, DataTableOperator.LessThan, DataTableOperator.LessOrEqual, DataTableOperator.Between, DataTableOperator.IsEmpty, DataTableOperator.IsNotEmpty],
        [DataTableValueKind.YesNo] = [DataTableOperator.Equals, DataTableOperator.NotEquals, DataTableOperator.IsEmpty, DataTableOperator.IsNotEmpty],
        [DataTableValueKind.List] = [DataTableOperator.Contains, DataTableOperator.IsEmpty, DataTableOperator.IsNotEmpty],
    };

    public static DataTableSearchPlan Of(DataTableSchema schema, DataTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit < 0)
            throw new DataTableQueryException("'limit' and 'offset' cannot be negative.");

        var where = (query.Where ?? []).Select(f => Plan(schema, f)).ToList();
        var sorts = (query.OrderBy ?? []).Select(s => new DataTablePlannedSort(ColumnNamed(schema, s.Column), KindOf(schema, s.Column), s.Descending)).ToList();
        return new DataTableSearchPlan(where, sorts, query.Limit, query.Offset);
    }

    /// <summary>The column of that name, as the table spells it — or why there is none.</summary>
    public static string ColumnNamed(DataTableSchema schema, string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        var system = SystemColumns.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        if (system is not null)
            return system;

        return schema.GetColumn(trimmed)?.Name ?? throw new DataTableQueryException($"There is no column '{name}' in this table. Its columns are: {string.Join(", ", SystemColumns.Concat(schema.Columns.Select(c => c.Name)))}");
    }

    /// <summary>What the column of that name holds, as a search compares it.</summary>
    public static DataTableValueKind KindOf(DataTableSchema schema, string column) =>
        ColumnNamed(schema, column) switch
        {
            "id" => DataTableValueKind.Number,
            "createdAt" or "updatedAt" => DataTableValueKind.Date,
            var name => schema.GetColumn(name)!.Type switch
            {
                ArgumentType.Number => DataTableValueKind.Number,
                ArgumentType.Boolean => DataTableValueKind.YesNo,
                ArgumentType.TextList => DataTableValueKind.List,
                _ => DataTableValueKind.Text,
            },
        };

    private static DataTablePlannedFilter Plan(DataTableSchema schema, DataTableFilter filter) =>
        filter switch
        {
            DataTableGroup group => new DataTablePlannedGroup(group.AnyOf, group.Items.Select(i => Plan(schema, i)).ToList()),
            DataTableCondition condition => Plan(schema, condition),
            _ => throw new DataTableQueryException("A condition is { \"column\": …, \"op\": …, \"value\": … }, a group { \"any\": [ … ] } or { \"all\": [ … ] }."),
        };

    private static DataTablePlannedCondition Plan(DataTableSchema schema, DataTableCondition condition)
    {
        var column = ColumnNamed(schema, condition.Column);
        var kind = KindOf(schema, column);
        var op = condition.Operator;
        var (value, value2) = (DataTableSearch.Unwrap(condition.Value), DataTableSearch.Unwrap(condition.Value2));

        // "= nothing" is "is empty": what an agent means when it sends a null.
        if (value is null && op is DataTableOperator.Equals or DataTableOperator.NotEquals)
            op = op == DataTableOperator.Equals ? DataTableOperator.IsEmpty : DataTableOperator.IsNotEmpty;
        if (op == DataTableOperator.Between && value2 is null && value is IList { Count: 2 } pair)
            (value, value2) = (pair[0], pair[1]);

        if (!Allowed[kind].Contains(op))
            throw new DataTableQueryException($"'{DataTableOperators.NameOf(op)}' does not work on '{column}', which holds {Describe(kind)}. Use one of: {string.Join(", ", Allowed[kind].Select(DataTableOperators.NameOf))}.");

        switch (op)
        {
            case DataTableOperator.IsEmpty or DataTableOperator.IsNotEmpty:
                return new DataTablePlannedCondition(column, kind, op, null, null);
            case DataTableOperator.Between:
                if (value is null || value2 is null)
                    throw new DataTableQueryException($"'between' needs two values on '{column}': the lowest and the highest, as \"value\": [low, high].");
                return new DataTablePlannedCondition(column, kind, op, Convert(column, kind, value), Convert(column, kind, value2));
        }

        // A list is searched for one of its items: a text.
        return new DataTablePlannedCondition(column, kind, op, Convert(column, kind == DataTableValueKind.List ? DataTableValueKind.Text : kind, value!), null);
    }

    private static string Describe(DataTableValueKind kind) =>
        kind switch
        {
            DataTableValueKind.Number => "numbers",
            DataTableValueKind.Date => "dates",
            DataTableValueKind.YesNo => "yes/no values",
            DataTableValueKind.List => "lists of texts",
            _ => "texts",
        };

    private static object Convert(string column, DataTableValueKind kind, object value) =>
        kind switch
        {
            DataTableValueKind.Number => DataTableSearch.AsNumber(value) ?? throw new DataTableQueryException($"'{column}' holds numbers: '{DataTableQueryJson.Show(value)}' is not a number."),
            DataTableValueKind.YesNo => DataTableSearch.AsYesNo(value) ?? throw new DataTableQueryException($"'{column}' holds yes/no values: '{DataTableQueryJson.Show(value)}' is neither true nor false."),
            DataTableValueKind.Date => DataTableSearch.AsDate(value) ?? throw new DataTableQueryException($"'{column}' holds dates: '{DataTableQueryJson.Show(value)}' is not a date. Write it like 2026-10-16 or 2026-10-16T09:30:00Z."),
            _ => DataTableSearch.Text(value),
        };
}

/// <summary>
/// A search done in memory, on the rows of a table held there (see <see cref="DataTableSearchPlan"/> for what it finds): texts
/// without regard to case, numbers as numbers, yes/no as yes/no, lists by their items, the row's id and dates as a number and dates.
/// </summary>
internal static class DataTableSearch
{
    public static DataTableSearchResult Run(DataTableSchema schema, IReadOnlyList<DataTableRow> rows, DataTableQuery query)
    {
        var plan = DataTableSearchPlan.Of(schema, query);
        var found = rows.Where(row => plan.Where.All(filter => Matches(row, filter))).ToList();
        found.Sort((a, b) =>
        {
            foreach (var (column, kind, descending) in plan.OrderBy)
            {
                var (x, y) = (Read(a, column, kind), Read(b, column, kind));
                // Empty values last, whichever way the column is sorted.
                var order = x is null ? (y is null ? 0 : 1) : y is null ? -1 : (descending ? -1 : 1) * Compare(kind, x, y);
                if (order != 0)
                    return order;
            }

            return a.Id.CompareTo(b.Id);
        });

        var page = found.Skip(plan.Offset).Take(plan.Limit ?? int.MaxValue).ToList();
        return new DataTableSearchResult(page, found.Count, plan.Offset);
    }

    public static string ColumnNamed(DataTableSchema schema, string? name) => DataTableSearchPlan.ColumnNamed(schema, name);

    public static string[] SystemColumns => DataTableSearchPlan.SystemColumns;

    private static bool Matches(DataTableRow row, DataTablePlannedFilter filter) =>
        filter switch
        {
            DataTablePlannedGroup { Items.Count: 0 } => true,
            DataTablePlannedGroup group => group.AnyOf ? group.Items.Any(i => Matches(row, i)) : group.Items.All(i => Matches(row, i)),
            DataTablePlannedCondition condition => Matches(row, condition),
            _ => false,
        };

    private static bool Matches(DataTableRow row, DataTablePlannedCondition condition)
    {
        var (column, kind, op, expected, high) = condition;
        var v = Read(row, column, kind);
        switch (op)
        {
            case DataTableOperator.IsEmpty:
                return IsEmpty(v);
            case DataTableOperator.IsNotEmpty:
                return !IsEmpty(v);
            case DataTableOperator.Between:
                return v is not null && Compare(kind, v, expected!) >= 0 && Compare(kind, v, high!) <= 0;
        }

        if (kind == DataTableValueKind.List)
            return v is IEnumerable<string> items && items.Any(i => string.Equals(i, (string)expected!, StringComparison.OrdinalIgnoreCase));

        return op switch
        {
            DataTableOperator.Equals => v is not null && Compare(kind, v, expected!) == 0,
            DataTableOperator.NotEquals => v is null || Compare(kind, v, expected!) != 0,
            DataTableOperator.Contains => v is string text && text.Contains((string)expected!, StringComparison.OrdinalIgnoreCase),
            DataTableOperator.StartsWith => v is string start && start.StartsWith((string)expected!, StringComparison.OrdinalIgnoreCase),
            DataTableOperator.GreaterThan => v is not null && Compare(kind, v, expected!) > 0,
            DataTableOperator.GreaterOrEqual => v is not null && Compare(kind, v, expected!) >= 0,
            DataTableOperator.LessThan => v is not null && Compare(kind, v, expected!) < 0,
            _ => v is not null && Compare(kind, v, expected!) <= 0,
        };
    }

    // The row's value, in the form the comparisons use: string, double, bool, DateTime or a list of strings — null when empty.
    private static object? Read(DataTableRow row, string column, DataTableValueKind kind)
    {
        object? raw = column switch
        {
            "id" => row.Id,
            "createdAt" => row.CreatedAt,
            "updatedAt" => row.UpdatedAt,
            _ => Unwrap(row.Get(column)),
        };
        if (raw is null)
            return null;

        return kind switch
        {
            DataTableValueKind.Number => AsNumber(raw),
            DataTableValueKind.YesNo => AsYesNo(raw),
            DataTableValueKind.Date => AsDate(raw),
            DataTableValueKind.List => raw is IEnumerable items and not string ? items.Cast<object?>().Select(i => Text(i)).ToList() : new List<string> { Text(raw) },
            _ => Text(raw),
        };
    }

    private static int Compare(DataTableValueKind kind, object x, object y) =>
        kind switch
        {
            DataTableValueKind.Number => ((double)x).CompareTo((double)y),
            DataTableValueKind.YesNo => ((bool)x).CompareTo((bool)y),
            DataTableValueKind.Date => ((DateTime)x).CompareTo((DateTime)y),
            DataTableValueKind.List => StringComparer.OrdinalIgnoreCase.Compare(string.Join(", ", (IEnumerable<string>)x), y as string ?? string.Join(", ", (IEnumerable<string>)y)),
            _ => StringComparer.OrdinalIgnoreCase.Compare((string)x, (string)y),
        };

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        IEnumerable<string> items => !items.Any(),
        _ => false,
    };

    // A JSON value (from a tool call, a node, the API) as a plain one.
    internal static object? Unwrap(object? value) =>
        value is JsonElement json
            ? json.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number => json.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => json.EnumerateArray().Select(e => Unwrap(e)).ToList(),
                _ => json.GetRawText(),
            }
            : value;

    internal static double? AsNumber(object value) =>
        value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };

    internal static bool? AsYesNo(object value) =>
        value switch
        {
            bool b => b,
            double d when d is 0 or 1 => d == 1,
            int i when i is 0 or 1 => i == 1,
            long l when l is 0 or 1 => l == 1,
            string s => s.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" => false,
                _ => null,
            },
            _ => null,
        };

    internal static DateTime? AsDate(object value) =>
        value switch
        {
            DateTime d => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc),
            DateTimeOffset o => o.UtcDateTime,
            string s when DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => null,
        };

    internal static string Text(object? value) => value is string s ? s : DataTableQueryJson.Show(value);
}
