using System.Text.RegularExpressions;

namespace Agentivity.Module.Assistant.Loop;

/// <summary>
/// Spots an answer that says something was saved or published. The assistant cannot save: such a claim is wrong and the person
/// would act on it. A sentence is a claim when it says "saved" (or "published") and nothing in it says otherwise ("nothing is
/// saved yet", "once you save it", "review and save").
/// </summary>
public static partial class SavedClaims
{
    public static bool Claims(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var sentence in Sentences().Split(text))
        {
            if (Saying().IsMatch(sentence) && !Negation().IsMatch(sentence))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?<=[.!?\n])\s+")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"\b(saved|published|stored in your workspace|added to your workspace|enregistr[ée]e?s?|sauvegard[ée]e?s?|publi[ée]e?s?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Saying();

    [GeneratedRegex(@"\b(not|nothing|never|until|before|once|unsaved|can't|cannot|can not|haven't|hasn't|isn't|aren't|won't|yet|if|when|after|to save|you save|you can save|you review|review|pas|rien|jamais|avant|tant que|quand|une fois|vous pouvez|à vous|encore)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negation();
}
