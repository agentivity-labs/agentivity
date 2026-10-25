using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Core.Json;

/// <summary>
/// Provides utility methods for text manipulation commonly used in workflow nodes.
/// </summary>
public static class TextHelpers
{
    private static readonly Regex WordCountRegex = new(@"\b\w+\b", RegexOptions.Compiled);

    /// <summary>
    /// Truncates text to a maximum number of words, adding ellipsis if truncated.
    /// </summary>
    /// <param name="text">The text to truncate.</param>
    /// <param name="maxWords">Maximum number of words to keep.</param>
    /// <returns>The truncated text.</returns>
    public static string TruncateWords(string? text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords)
        {
            return text;
        }

        return string.Join(' ', words[..maxWords]) + "...";
    }

    /// <summary>
    /// Truncates text to a maximum number of characters, adding ellipsis if truncated.
    /// </summary>
    /// <param name="text">The text to truncate.</param>
    /// <param name="maxChars">Maximum number of characters to keep.</param>
    /// <returns>The truncated text.</returns>
    public static string TruncateChars(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars].TrimEnd() + "...";
    }

    /// <summary>
    /// Counts the number of words in a text using word boundary matching.
    /// More accurate than simple space splitting.
    /// </summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return WordCountRegex.Matches(text).Count;
    }

    /// <summary>
    /// Strips markdown code fences (``` blocks) from LLM responses.
    /// </summary>
    public static string StripCodeFences(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var newlineIndex = trimmed.IndexOf('\n');
        if (newlineIndex >= 0)
        {
            trimmed = trimmed[(newlineIndex + 1)..];
        }

        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
        {
            trimmed = trimmed[..closing];
        }

        return trimmed.Trim();
    }

    /// <summary>
    /// Builds a markdown heading prefix for a given level.
    /// </summary>
    public static string BuildHeadingPrefix(int level) => new('#', Math.Clamp(level, 1, 6));

    /// <summary>
    /// Formats a section title with its heading level.
    /// </summary>
    public static string FormatHeading(string title, int level) => $"{BuildHeadingPrefix(level)} {title}";

    /// <summary>
    /// Wraps text in markdown italic.
    /// </summary>
    public static string Italic(string text) => $"_{text}_";

    /// <summary>
    /// Wraps text in markdown bold.
    /// </summary>
    public static string Bold(string text) => $"**{text}**";
}
