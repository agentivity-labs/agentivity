using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>
/// Reads operations written as plain lines instead of nested JSON:
/// <code>
/// create_entity kind=agent name="Shop assistant"
/// add_node entity="Shop assistant" type=ai.node inputs={"prompt":"Answer politely"}
/// </code>
/// One operation per line (or separated by ;), the name first, then <c>key=value</c> pairs. A value is a bare word, a double-quoted string, or a
/// JSON literal (object, array, number, true, false, null). Small models write this far more reliably than an array of
/// objects; the result is the same <see cref="BuilderOperation"/> the JSON form gives.
/// </summary>
public static class CommandParser
{
    /// <summary>The operations, or the first line that could not be read and why.</summary>
    public static (List<BuilderOperation> Operations, string? Error) Parse(string text)
    {
        var operations = new List<BuilderOperation>();
        var lineNumber = 0;
        foreach (var (rawLine, line0) in Statements(text ?? ""))
        {
            lineNumber = line0;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            var (operation, error) = ParseLine(line);
            if (operation is null)
                return ([], $"Line {lineNumber}: {error}");
            operations.Add(operation);
        }

        return (operations, operations.Count == 0 ? "There is no command in the text." : null);
    }

    /// <summary>
    /// The statements of the text: one per line, and also split at a ';' that is outside quotes and brackets, because models
    /// often put several commands on one line separated that way. Each comes with the line it started on.
    /// </summary>
    private static IEnumerable<(string Text, int Line)> Statements(string text)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;
        var line = 1;
        var startLine = 1;
        const char Lf = (char)10;
        var source = text.Replace("\r\n", "\n");
        for (var index = 0; index < source.Length; index++)
        {
            var ch = source[index];
            // A backslash followed by n, outside any text: the model wrote the line break as two characters.
            if (!inString && ch == (char)92 && index + 1 < source.Length && source[index + 1] == 'n')
            {
                ch = Lf;
                index++;
            }

            if (inString)
            {
                sb.Append(ch);
                if (escaped) escaped = false;
                else if (ch == (char)92) escaped = true;
                else if (ch == (char)34) inString = false;
                else if (ch == Lf) line++;
                continue;
            }

            if (ch == (char)34) { inString = true; sb.Append(ch); }
            else if (ch is '{' or '[') { depth++; sb.Append(ch); }
            else if (ch is '}' or ']') { depth = Math.Max(0, depth - 1); sb.Append(ch); }
            else if (depth == 0 && (ch == Lf || ch == ';'))
            {
                yield return (sb.ToString(), startLine);
                sb.Clear();
                if (ch == Lf) line++;
                startLine = line;
            }
            else if (ch == Lf)
            {
                line++;
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
            yield return (sb.ToString(), startLine);
    }

    private static (BuilderOperation? Operation, string? Error) ParseLine(string line)
    {
        var i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]))
            i++;
        var name = line[..i];
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return (null, $"'{Shorten(name)}' is not an operation name; a line starts with the name, e.g. add_node entity=\"My flow\" type=…");

        var args = new JsonObject();
        while (true)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= line.Length)
                break;

            var keyStart = i;
            while (i < line.Length && line[i] != '=' && !char.IsWhiteSpace(line[i]))
                i++;
            var key = line[keyStart..i];
            if (i >= line.Length || line[i] != '=' || key.Length == 0)
                return (null, $"expected key=value after '{name}', found '{Shorten(line[keyStart..])}'.");
            i++;

            var (value, next, error) = ReadValue(line, i);
            if (error is not null)
                return (null, $"{key}: {error}");
            args[key] = value;
            i = next;
        }

        return (new BuilderOperation(name, args), null);
    }

    private static (JsonNode? Value, int Next, string? Error) ReadValue(string line, int start)
    {
        if (start >= line.Length)
            return (null, start, "a value is missing after '='.");

        var c = line[start];
        if (c == '"')
        {
            var end = start + 1;
            var escaped = false;
            while (end < line.Length)
            {
                if (escaped) escaped = false;
                else if (line[end] == '\\') escaped = true;
                else if (line[end] == '"') break;
                end++;
            }

            if (end >= line.Length)
                return (null, start, "a quoted text is not closed.");
            var literal = line[start..(end + 1)];
            try
            {
                return (JsonNode.Parse(literal), end + 1, null);
            }
            catch (JsonException)
            {
                return (null, start, "that quoted text is not valid.");
            }
        }

        if (c is '{' or '[')
        {
            var end = BalancedEnd(line, start);
            if (end < 0)
                return (null, start, "a { } or [ ] value is not closed.");
            try
            {
                return (JsonNode.Parse(line[start..(end + 1)]), end + 1, null);
            }
            catch (JsonException ex)
            {
                return (null, start, $"that JSON value is not valid ({ex.Message.Split('.')[0]}); use double quotes inside it.");
            }
        }

        var stop = start;
        while (stop < line.Length && !char.IsWhiteSpace(line[stop]))
            stop++;
        var token = line[start..stop];
        return (token switch
        {
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            "null" => null,
            _ when int.TryParse(token, out var n) => JsonValue.Create(n),
            _ when long.TryParse(token, out var big) => JsonValue.Create(big),
            _ when double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
            _ => JsonValue.Create(token),
        }, stop, null);
    }

    /// <summary>Index of the bracket closing the one at <paramref name="start"/>, skipping strings; -1 when it never closes.</summary>
    private static int BalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') inString = true;
            else if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
