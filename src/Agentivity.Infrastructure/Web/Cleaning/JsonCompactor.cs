using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Web.Cleaning;

/// <summary>
/// Makes a large JSON document small by taking out what carries no information — never by picking what to keep. It knows no
/// site and no field name. Stage one drops what is empty or meaningless on its face (empty and false values, identifiers,
/// timestamps, markup fragments, static-file addresses), collapses repeats, and cuts long strings and long lists. When that is
/// still too large, stage two drops what <em>the page a person reads does not show</em> — first whole sub-trees with nothing on the page,
/// then single values: a framework's configuration never shows on the page, the product does.
/// </summary>
internal static partial class JsonCompactor
{
    // Progressively stricter limits, tried in turn until the result fits: (list items kept, string length kept).
    private static readonly (int Items, int Chars)[] Tiers = [(30, 400), (15, 200), (8, 120), (4, 80)];

    private const int MaxDepth = 30;

    /// <summary>A sub-tree smaller than this is not worth a decision of its own.</summary>
    private const int MinDroppableChars = 200;

    /// <summary>Only sub-trees under these top-level entries may be dropped for relevance; the others describe the page itself.</summary>
    private static readonly string[] DroppableRoots = ["state"];

    /// <summary>
    /// Compacts <paramref name="root"/> to at most <paramref name="maxChars"/> characters of JSON.
    /// <paramref name="visibleText"/> is the readable content of the same page, used to tell what the page is about.
    /// </summary>
    public static JsonNode? Compact(JsonElement root, int maxChars, CleaningStats stats, string? visibleText = null)
    {
        HashSet<string>? visible = visibleText is null ? null : Tokens(visibleText).ToHashSet(StringComparer.Ordinal);
        JsonNode? best = null;
        foreach ((int items, int chars) in Tiers)
        {
            best = Visit(root, 0, items, chars, new HashSet<string>(StringComparer.Ordinal));
            if (best is null || Size(best) <= maxChars)
            {
                return best;
            }

            if (visible is not null && best is JsonObject obj)
            {
                DropWhatThePageDoesNotShow(obj, maxChars, visible, stats);
                if (Size(best) <= maxChars)
                {
                    return best;
                }
            }
        }

        // Still too large at the strictest setting: drop whole top-level entries, the largest first, and say which.
        if (best is JsonObject top)
        {
            stats.StructuredTruncated = true;
            while (Size(top) > maxChars && top.Count > 0)
            {
                string largest = top.OrderByDescending(p => Size(p.Value)).First().Key;
                stats.DroppedData.Add($"{largest} ({Size(top[largest])} chars)");
                top.Remove(largest);
            }
        }

        return best is JsonObject { Count: > 0 } ? best : null;
    }

    // ── stage two: relevance to the page ────────────────────────────────────

    private readonly record struct Relatedness(int Informative, int Matched, int Size);

    /// <summary>At most this many dropped entries are listed individually in the stats; the rest are counted.</summary>
    private const int MaxListedDrops = 25;

    /// <summary>
    /// Brings a document under the limit by what the page does not show. First whole sub-trees with no value at all on the page
    /// (largest first — a framework's configuration, ad slots, layout descriptions); only if that is not enough, the single values
    /// that are not on the page. A sub-tree holding anything the page shows is never dropped as a whole.
    /// </summary>
    private static void DropWhatThePageDoesNotShow(JsonObject root, int maxChars, HashSet<string> visible, CleaningStats stats)
    {
        var measured = new Dictionary<JsonNode, Relatedness>(ReferenceEqualityComparer.Instance);
        foreach (string name in DroppableRoots)
        {
            if (root[name] is JsonNode subtree)
            {
                Measure(subtree, visible, measured);
            }
        }

        // (a) the maximal sub-trees with nothing on the page, largest first
        var unrelated = new List<JsonNode>();
        foreach (string name in DroppableRoots)
        {
            if (root[name] is JsonNode subtree)
            {
                CollectUnrelated(subtree, measured, unrelated);
            }
        }

        int dropped = 0;
        foreach (JsonNode node in unrelated.OrderByDescending(n => measured[n].Size))
        {
            if (Size(root) <= maxChars)
            {
                break;
            }

            Note(stats, $"{node.GetPath()} ({Size(node)} chars, nothing of it on the page)", ref dropped);
            Detach(node);
        }

        // (b) still too large: the values the page does not show, one by one
        if (Size(root) > maxChars)
        {
            int removed = 0;
            foreach (string name in DroppableRoots)
            {
                if (root[name] is JsonNode subtree)
                {
                    removed += RemoveUnmatchedValues(subtree, visible);
                }
            }

            if (removed > 0)
            {
                Note(stats, $"{removed} values that are not on the page", ref dropped);
            }
        }

        Prune(root);
        if (dropped > MaxListedDrops)
        {
            stats.DroppedData.Add($"… and {dropped - MaxListedDrops} more");
        }
    }

    private static void Note(CleaningStats stats, string what, ref int count)
    {
        stats.StructuredTruncated = true;
        if (++count <= MaxListedDrops)
        {
            stats.DroppedData.Add(what);
        }
    }

    private static void CollectUnrelated(JsonNode node, Dictionary<JsonNode, Relatedness> measured, List<JsonNode> into)
    {
        if (node is not (JsonObject or JsonArray) || !measured.TryGetValue(node, out Relatedness info))
        {
            return;
        }

        if (info.Informative > 0 && info.Matched == 0 && info.Size >= MinDroppableChars)
        {
            into.Add(node);
            return; // maximal: its own descendants go with it
        }

        IEnumerable<JsonNode?> children = node is JsonObject obj ? obj.Select(p => p.Value) : ((JsonArray)node);
        foreach (JsonNode? child in children.ToList())
        {
            if (child is not null)
            {
                CollectUnrelated(child, measured, into);
            }
        }
    }

    private static int RemoveUnmatchedValues(JsonNode node, HashSet<string> visible)
    {
        int removed = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToList())
                {
                    if (property.Value is JsonValue leaf && IsUnmatched(leaf, visible))
                    {
                        obj.Remove(property.Key);
                        removed++;
                    }
                    else if (property.Value is not null)
                    {
                        removed += RemoveUnmatchedValues(property.Value, visible);
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array.ToList())
                {
                    if (item is JsonValue leaf && IsUnmatched(leaf, visible))
                    {
                        array.Remove(item);
                        removed++;
                    }
                    else if (item is not null)
                    {
                        removed += RemoveUnmatchedValues(item, visible);
                    }
                }

                break;
        }

        return removed;
    }

    private static bool IsUnmatched(JsonValue leaf, HashSet<string> visible)
    {
        List<string> tokens = Tokens(leaf.ToJsonString().Trim('"')).ToList();
        return tokens.Count > 0 && tokens.Count(visible.Contains) * 5 < tokens.Count * 3; // informative, and under 60 % of it is on the page
    }

    private static Relatedness Measure(JsonNode node, HashSet<string> visible, Dictionary<JsonNode, Relatedness> measured)
    {
        Relatedness result;
        switch (node)
        {
            case JsonObject obj:
            {
                int informative = 0, matched = 0, size = 2;
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    if (property.Value is null)
                    {
                        continue;
                    }

                    Relatedness child = Measure(property.Value, visible, measured);
                    informative += child.Informative;
                    matched += child.Matched;
                    size += child.Size + property.Key.Length + 4;
                }

                result = new Relatedness(informative, matched, size);
                break;
            }

            case JsonArray array:
            {
                int informative = 0, matched = 0, size = 2;
                foreach (JsonNode? item in array)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    Relatedness child = Measure(item, visible, measured);
                    informative += child.Informative;
                    matched += child.Matched;
                    size += child.Size + 1;
                }

                result = new Relatedness(informative, matched, size);
                break;
            }

            default:
            {
                string text = node.ToJsonString().Trim('"');
                List<string> tokens = Tokens(text).ToList();
                result = tokens.Count == 0
                    ? new Relatedness(0, 0, text.Length + 2)
                    : new Relatedness(1, tokens.Count(visible.Contains) * 5 >= tokens.Count * 3 ? 1 : 0, text.Length + 2); // at least 60 % of its words are on the page
                break;
            }
        }

        measured[node] = result;
        return result;
    }

    /// <summary>The words and numbers of a text, lowercase: three letters or more, or a number of two digits or more.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        foreach (Match match in Word().Matches(text))
        {
            string token = match.Value.ToLowerInvariant();
            if (token.Length >= 3 || (token.Length == 2 && char.IsDigit(token[0]) && char.IsDigit(token[1])))
            {
                yield return token;
            }
        }
    }

    private static void Detach(JsonNode node)
    {
        switch (node.Parent)
        {
            case JsonObject parent:
                parent.Remove(node.GetPropertyName());
                break;
            case JsonArray parent:
                parent.Remove(node);
                break;
        }
    }

    /// <summary>Removes the containers left empty once their contents were dropped.</summary>
    private static bool Prune(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonNode child && Prune(child))
                    {
                        obj.Remove(key);
                    }
                }

                return obj.Count == 0;
            case JsonArray array:
                foreach (JsonNode? child in array.ToList())
                {
                    if (child is not null && Prune(child))
                    {
                        array.Remove(child);
                    }
                }

                return array.Count == 0;
            default:
                return false;
        }
    }

    private static int Size(JsonNode? node) => node?.ToJsonString().Length ?? 0;

    // ── stage one: what carries no information ──────────────────────────────

    private static JsonNode? Visit(JsonElement element, int depth, int maxItems, int maxChars, HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (depth >= MaxDepth)
                {
                    return null;
                }

                var result = new JsonObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.StartsWith("__", StringComparison.Ordinal))
                    {
                        continue; // GraphQL "__typename" and the like: type tags, not content
                    }

                    JsonNode? child = Visit(property.Value, depth + 1, maxItems, maxChars, seen);
                    if (child is not null)
                    {
                        result[property.Name] = child;
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonValueKind.Array:
            {
                if (depth >= MaxDepth)
                {
                    return null;
                }

                var result = new JsonArray();
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement item in element.EnumerateArray())
                {
                    JsonNode? child = Visit(item, depth + 1, maxItems, maxChars, seen);
                    if (child is null || !distinct.Add(child.ToJsonString()))
                    {
                        continue;
                    }

                    result.Add(child);
                    if (result.Count >= maxItems)
                    {
                        break;
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonValueKind.String:
                return VisitString(element.GetString(), maxChars, seen);

            case JsonValueKind.Number:
                return IsEpochTimestamp(element) ? null : JsonNode.Parse(element.GetRawText());

            case JsonValueKind.True:
                return JsonValue.Create(true); // a false flag says nothing; a true one may ("in stock")

            default:
                return null; // null, false
        }
    }

    private static JsonNode? VisitString(string? value, int maxChars, HashSet<string> seen)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || MarkupFragment().IsMatch(text) || StaticFileAddress().IsMatch(text) || LooksLikeIdentifier(text))
        {
            return null;
        }

        if (text.Length > maxChars)
        {
            text = text[..maxChars] + "…";
        }

        // The same long string appearing again (an image address repeated per variant) adds nothing.
        if (text.Length >= 30 && !seen.Add(text))
        {
            return null;
        }

        return JsonValue.Create(text);
    }

    /// <summary>Unix seconds / milliseconds between 2001 and 2033: a date stamp a person never reads as a number.</summary>
    private static bool IsEpochTimestamp(JsonElement number)
    {
        if (!number.TryGetInt64(out long n))
        {
            return false;
        }

        return (n >= 1_000_000_000 && n <= 2_000_000_000) || (n >= 1_000_000_000_000 && n <= 2_000_000_000_000);
    }

    private static bool LooksLikeIdentifier(string text) => Uuid().IsMatch(text) || LongHex().IsMatch(text) || LongBase64().IsMatch(text);

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9]*[\s/>]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupFragment();

    [GeneratedRegex(@"^(https?:)?//\S+\.(js|css|woff2?|ttf|eot|map|ico)(\?\S*)?$|^/\S+\.(js|css|woff2?|ttf|eot|map|ico)(\?\S*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaticFileAddress();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"^[0-9a-fA-F]{24,}$", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();

    // Base64 has no spaces, no hyphens and no dots, so an ordinary slug or sentence never matches.
    [GeneratedRegex(@"^[A-Za-z0-9+/]{40,}={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex LongBase64();
}
