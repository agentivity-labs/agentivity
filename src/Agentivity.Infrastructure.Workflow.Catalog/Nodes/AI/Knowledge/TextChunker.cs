using System.Text;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public enum TextSplitStrategy
{
    RecursiveCharacter,
    Token,
}

/// <summary>
/// Pure-C# text chunking — no tokenizer/NLP dependency, deliberately (EPIC-0462 P1-7). Two
/// strategies:
/// <list type="bullet">
///   <item><see cref="TextSplitStrategy.RecursiveCharacter"/> — tries to split on the largest
///   semantic boundary first (paragraph, then line, then sentence, then word), only falling back
///   to a hard character cut for a single run of text with none of those boundaries. Mirrors the
///   spirit of LangChain's RecursiveCharacterTextSplitter without reproducing it exactly.</item>
///   <item><see cref="TextSplitStrategy.Token"/> — an approximate, word-count-based stand-in for
///   real tokenization (1 word ≈ 1 token). Good enough to keep chunks under a model's context
///   budget without pulling in a real tokenizer; not exact.</item>
/// </list>
/// </summary>
public static class TextChunker
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", " "];

    public static IReadOnlyList<string> Split(string? text, int chunkSize, int chunkOverlap, TextSplitStrategy strategy)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        chunkSize = Math.Max(1, chunkSize);
        chunkOverlap = Math.Clamp(chunkOverlap, 0, chunkSize - 1);

        return strategy == TextSplitStrategy.Token ? SplitByApproxTokens(text, chunkSize, chunkOverlap) : SplitRecursiveCharacter(text, chunkSize, chunkOverlap);
    }

    private static List<string> SplitRecursiveCharacter(string text, int chunkSize, int chunkOverlap)
    {
        List<string> atoms = SplitIntoAtoms(text, chunkSize, 0);
        return MergeAtomsWithOverlap(atoms, chunkSize, chunkOverlap);
    }

    // Recursively breaks text into pieces no larger than chunkSize, preferring to break on the
    // largest available boundary before falling back to the next, finer one.
    private static List<string> SplitIntoAtoms(string text, int chunkSize, int separatorIndex)
    {
        if (text.Length <= chunkSize)
        {
            return [text];
        }

        if (separatorIndex >= Separators.Length)
        {
            // No separator left that helps (e.g. one very long unbroken run) — hard character cut.
            var hardCut = new List<string>();
            for (var i = 0; i < text.Length; i += chunkSize)
            {
                hardCut.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
            }
            return hardCut;
        }

        string separator = Separators[separatorIndex];
        string[] parts = text.Split(separator);
        if (parts.Length == 1)
        {
            // This separator doesn't occur in the text at all — try the next, finer one.
            return SplitIntoAtoms(text, chunkSize, separatorIndex + 1);
        }

        var atoms = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            // Re-attach the separator to keep it as part of the text (except after the final
            // piece, which had nothing after it to begin with).
            string piece = i < parts.Length - 1 ? parts[i] + separator : parts[i];
            if (piece.Length == 0)
            {
                continue;
            }

            atoms.AddRange(SplitIntoAtoms(piece, chunkSize, separatorIndex + 1));
        }

        return atoms;
    }

    // Greedily packs atoms into chunks up to chunkSize, seeding each new chunk with the trailing
    // chunkOverlap characters of the previous one so consecutive chunks share context.
    private static List<string> MergeAtomsWithOverlap(List<string> atoms, int chunkSize, int chunkOverlap)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (string atom in atoms)
        {
            if (current.Length > 0 && current.Length + atom.Length > chunkSize)
            {
                chunks.Add(current.ToString());
                string tail = chunkOverlap > 0 ? TakeLast(current.ToString(), chunkOverlap) : string.Empty;
                current.Clear();
                current.Append(tail);
            }

            current.Append(atom);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private static string TakeLast(string value, int count) => count >= value.Length ? value : value[^count..];

    private static List<string> SplitByApproxTokens(string text, int chunkSizeWords, int overlapWords)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        var chunks = new List<string>();
        var start = 0;
        int step = Math.Max(1, chunkSizeWords - overlapWords);

        while (start < words.Length)
        {
            int count = Math.Min(chunkSizeWords, words.Length - start);
            chunks.Add(string.Join(' ', words.Skip(start).Take(count)));

            if (start + count >= words.Length)
            {
                break;
            }

            start += step;
        }

        return chunks;
    }
}
