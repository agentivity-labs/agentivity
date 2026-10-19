using System.Text;
using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.KnowledgeBases.Engine;

/// <summary>A passage cut from a document: its text, headed by where it is (the title of the document and its section), and that section.</summary>
public sealed record KnowledgeChunk(string Text, string? Section);

/// <summary>
/// How the engine cuts a document into passages: by its sections first (Markdown headings), then by paragraphs and sentences when
/// a section is long. Each passage starts with the title of its document and its section ("Returns policy &gt; Delay"), so that a
/// passage read alone still says what it is about, and is found by it. Passages are short enough for the local model to read
/// them whole.
/// </summary>
public static partial class KnowledgeChunker
{
    /// <summary>The longest passage, in characters (the local model reads about 250 words).</summary>
    public const int PassageSize = 900;

    private const int Overlap = 100;

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    /// <summary>The title of the document: the one it was given, else its first top-level heading, else <paramref name="fallback"/>.</summary>
    public static string TitleOf(string text, string? given, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return given.Trim();
        foreach (var line in text.Split('\n'))
        {
            var match = Heading().Match(line.TrimEnd('\r'));
            if (match.Success && match.Groups[1].Length == 1)
                return match.Groups[2].Value.Trim();
        }

        return fallback;
    }

    public static IReadOnlyList<KnowledgeChunk> Split(string text, string title)
    {
        var chunks = new List<KnowledgeChunk>();
        var headings = new string?[7];
        var body = new StringBuilder();

        void Flush()
        {
            var content = body.ToString().Trim();
            body.Clear();
            if (content.Length == 0)
                return;
            var section = string.Join(" > ", headings.Skip(2).Where(h => !string.IsNullOrWhiteSpace(h)));
            var head = section.Length == 0 ? title : $"{title} > {section}";
            foreach (var piece in TextChunker.Split(content, PassageSize - head.Length - 1, Overlap, TextSplitStrategy.RecursiveCharacter))
                chunks.Add(new KnowledgeChunk($"{head}\n{piece.Trim()}", section.Length == 0 ? null : section));
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Heading().Match(raw);
            if (!match.Success)
            {
                body.AppendLine(raw);
                continue;
            }

            Flush();
            var level = match.Groups[1].Length;
            headings[level] = match.Groups[2].Value.Trim();
            for (var deeper = level + 1; deeper < headings.Length; deeper++)
                headings[deeper] = null;
        }

        Flush();
        return chunks;
    }
}
