using System.Globalization;
using System.Text.Json;

namespace Agentivity.Domain.DataTables;

/// <summary>How a condition compares a column to its value.</summary>
public enum DataTableOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Between,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>What a row must be to be found: a condition, or a group of them.</summary>
public abstract record DataTableFilter;

/// <summary>One condition of a search: <c>column operator value</c> (<c>between</c> takes <see cref="Value2"/> too).</summary>
public sealed record DataTableCondition(string Column, DataTableOperator Operator, object? Value = null, object? Value2 = null) : DataTableFilter;

/// <summary>A group of conditions (and groups): all of them must be true, or — <see cref="AnyOf"/> — one of them at least.</summary>
public sealed record DataTableGroup(bool AnyOf, IReadOnlyList<DataTableFilter> Items) : DataTableFilter;

/// <summary>One column to sort the rows found on.</summary>
public sealed record DataTableSort(string Column, bool Descending = false);

/// <summary>
/// A search in a Data Table: rows for which every condition (or group) is true, sorted (by id when nothing is said), from
/// <see cref="Offset"/>, at most <see cref="Limit"/> of them (none: every row found — a table kept in a database gives a page at
/// most; 0: none, only the total).
/// </summary>
public sealed record DataTableQuery(IReadOnlyList<DataTableFilter>? Where = null, IReadOnlyList<DataTableSort>? OrderBy = null, int? Limit = null, int Offset = 0);

/// <summary>The rows of one page of a search, and how many rows the search found in all.</summary>
public sealed record DataTableSearchResult(IReadOnlyList<DataTableRow> Rows, int Total, int Offset)
{
    public bool HasMore => Offset + Rows.Count < Total;

    /// <summary>Where the next page starts, when there is one.</summary>
    public int? NextOffset => HasMore && Rows.Count > 0 ? Offset + Rows.Count : null;
}

/// <summary>A search that cannot be done, and why, in words the person — or the agent — can correct it with.</summary>
public sealed class DataTableQueryException(string message) : ArgumentException(message);

/// <summary>The operators, by the names people and agents write them with.</summary>
public static class DataTableOperators
{
    /// <summary>The names shown — in a tool's description, in an error.</summary>
    public static IReadOnlyList<string> Names { get; } = ["=", "!=", "contains", "starts_with", ">", ">=", "<", "<=", "between", "is_empty", "is_not_empty"];

    private static readonly Dictionary<string, DataTableOperator> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = DataTableOperator.Equals,
        ["=="] = DataTableOperator.Equals,
        ["eq"] = DataTableOperator.Equals,
        ["equals"] = DataTableOperator.Equals,
        ["!="] = DataTableOperator.NotEquals,
        ["<>"] = DataTableOperator.NotEquals,
        ["ne"] = DataTableOperator.NotEquals,
        ["not_equals"] = DataTableOperator.NotEquals,
        ["contains"] = DataTableOperator.Contains,
        ["starts_with"] = DataTableOperator.StartsWith,
        ["startswith"] = DataTableOperator.StartsWith,
        [">"] = DataTableOperator.GreaterThan,
        ["gt"] = DataTableOperator.GreaterThan,
        [">="] = DataTableOperator.GreaterOrEqual,
        ["gte"] = DataTableOperator.GreaterOrEqual,
        ["<"] = DataTableOperator.LessThan,
        ["lt"] = DataTableOperator.LessThan,
        ["<="] = DataTableOperator.LessOrEqual,
        ["lte"] = DataTableOperator.LessOrEqual,
        ["between"] = DataTableOperator.Between,
        ["is_empty"] = DataTableOperator.IsEmpty,
        ["empty"] = DataTableOperator.IsEmpty,
        ["is_not_empty"] = DataTableOperator.IsNotEmpty,
        ["not_empty"] = DataTableOperator.IsNotEmpty,
    };

    public static DataTableOperator Parse(string? text)
    {
        var name = (text ?? string.Empty).Trim().Replace(' ', '_').Replace('-', '_');
        return ByName.TryGetValue(name, out var found) ? found : throw new DataTableQueryException($"'{text}' is not an operator. Use one of: {string.Join(", ", Names)}.");
    }

    public static string NameOf(DataTableOperator op) => Names[(int)op];
}

/// <summary>
/// A search written as JSON — by an agent's tool call, in a workflow node, in the API:
/// <c>where</c> is a list of <c>{ "column", "op", "value" }</c> (<c>between</c>: <c>"value": [low, high]</c>), all of which must be
/// true; a group <c>{ "any": [ … ] }</c> is true when one of its conditions is, <c>{ "all": [ … ] }</c> when they all are, and groups
/// go into groups. <c>order_by</c> is <c>"amount desc, customer"</c> or a list of <c>{ "column", "direction" }</c>.
/// </summary>
public static class DataTableQueryJson
{
    public static IReadOnlyList<DataTableFilter> ParseWhere(JsonElement where)
    {
        if (where.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (where.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(where.GetString()) ? [] : ParseWhere(where.GetString());
        if (where.ValueKind == JsonValueKind.Object)
            return [Filter(where)];
        if (where.ValueKind != JsonValueKind.Array)
            throw new DataTableQueryException("'where' is a list of conditions, each { \"column\": …, \"op\": …, \"value\": … }, or groups { \"any\": [ … ] }.");
        return where.EnumerateArray().Select(Filter).ToList();
    }

    public static IReadOnlyList<DataTableFilter> ParseWhere(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseWhere(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new DataTableQueryException($"'where' is not valid JSON ({ex.Message}). Write it as a list of conditions, each {{ \"column\": …, \"op\": …, \"value\": … }}.");
        }
    }

    public static IReadOnlyList<DataTableSort> ParseOrderBy(JsonElement orderBy) =>
        orderBy.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            JsonValueKind.String => ParseOrderBy(orderBy.GetString()),
            JsonValueKind.Object => [Sort(orderBy)],
            JsonValueKind.Array => orderBy.EnumerateArray().SelectMany(item => item.ValueKind == JsonValueKind.String ? ParseOrderBy(item.GetString()) : [Sort(item)]).ToList(),
            _ => throw new DataTableQueryException("'order_by' is a column name, or a list of { \"column\": …, \"direction\": \"asc\" or \"desc\" }."),
        };

    /// <summary><c>"amount desc, customer"</c>: columns separated by commas, each followed by <c>asc</c> or <c>desc</c> if need be.</summary>
    public static IReadOnlyList<DataTableSort> ParseOrderBy(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part =>
                {
                    var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return new DataTableSort(words[0], words.Length > 1 && Descending(words[1]));
                })
                .ToList();

    // A condition, or a group: { "any": [ … ] } (one of them true) or { "all": [ … ] } (all of them true).
    private static DataTableFilter Filter(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var (name, anyOf) in new[] { ("any", true), ("or", true), ("all", false), ("and", false) })
            {
                if (item.TryGetProperty(name, out var items))
                {
                    if (items.ValueKind != JsonValueKind.Array)
                        throw new DataTableQueryException($"'{name}' is a list of conditions (or groups): {{ \"{name}\": [ {{ \"column\": …, \"op\": …, \"value\": … }}, … ] }}.");
                    return new DataTableGroup(anyOf, items.EnumerateArray().Select(Filter).ToList());
                }
            }
        }

        return Condition(item);
    }

    private static DataTableCondition Condition(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("column", out var column) || column.ValueKind != JsonValueKind.String)
            throw new DataTableQueryException("Each condition is { \"column\": …, \"op\": …, \"value\": … }: its column is missing.");

        var op = item.TryGetProperty("op", out var o) || item.TryGetProperty("operator", out o) ? o.GetString() : "=";
        var value = item.TryGetProperty("value", out var v) ? v.Clone() : (object?)null;
        var value2 = item.TryGetProperty("value2", out var v2) ? v2.Clone() : (object?)null;
        return new DataTableCondition(column.GetString()!, DataTableOperators.Parse(op), value, value2);
    }

    private static DataTableSort Sort(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("column", out var column) || column.ValueKind != JsonValueKind.String)
            throw new DataTableQueryException("Each sort is { \"column\": …, \"direction\": \"asc\" or \"desc\" }: its column is missing.");
        var descending = item.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String && Descending(d.GetString()!)
            || item.TryGetProperty("descending", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new DataTableSort(column.GetString()!, descending);
    }

    private static bool Descending(string word) => word.Trim().StartsWith("desc", StringComparison.OrdinalIgnoreCase);

    internal static string Show(object? value) =>
        value switch
        {
            null => "null",
            JsonElement { ValueKind: JsonValueKind.String } text => text.GetString()!,
            JsonElement json => json.GetRawText(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
}
