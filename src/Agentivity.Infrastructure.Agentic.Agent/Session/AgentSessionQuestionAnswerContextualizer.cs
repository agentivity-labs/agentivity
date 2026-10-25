using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentSessionQuestionAnswerContextualizer
{
    private static readonly Regex MultipleWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string BuildResolvedValue(string question, string answer)
    {
        var normalizedQuestion = Normalize(question);
        var normalizedAnswer = Normalize(answer);

        if (string.IsNullOrWhiteSpace(normalizedAnswer))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            return normalizedAnswer;
        }

        return $"For the question '{normalizedQuestion}', the answer is '{normalizedAnswer}'.";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return MultipleWhitespaceRegex.Replace(value.Trim(), " ");
    }
}
