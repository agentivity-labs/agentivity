using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Sanitizes tool observation content before it is injected into the agent scratchpad.
/// Neutralizes prompt injection patterns that could hijack the reasoning loop by
/// impersonating ReAct role tokens, system instructions, or LLM control sequences.
///
/// Design: static, allocation-free on the clean path (no injection detected).
/// Applied only when <see cref="AgenticModeCapabilities.SanitizesObservations"/> is true.
///
/// Strategy: suspicious role-impersonating tokens are wrapped in a neutral literal
/// bracket rather than stripped, preserving the informational content for the agent
/// while breaking the injection semantic. This avoids false data loss on legitimate
/// content (e.g. a tool that returns documentation containing "Action:").
///
/// See ADR: architecture/decisions/react-observation-sanitization.md
/// </summary>
public static class ObservationSanitizer
{
    // Matches ReAct scratchpad role tokens and common system-level injection vectors
    // at the start of a line (after optional whitespace), case-insensitive.
    // Covers: Thought:, Action:, Observation:, System:, User:, Assistant:, Human:, AI:
    // and XML-style role/system tags used by some LLM providers.
    private static readonly Regex RoleTokenPattern = new(@"(?m)^[ \t]*(Thought|Action|Observation|System|User|Assistant|Human|AI)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromMilliseconds(100));

    private static readonly Regex XmlRoleTagPattern = new(@"<\s*/?\s*(system|user|assistant|human|ai|instruction|prompt)\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Returns sanitized observation content. Returns the original string unchanged
    /// when no injection pattern is detected (zero allocation on the clean path).
    /// </summary>
    public static string Sanitize(string observationContent)
    {
        if (string.IsNullOrEmpty(observationContent))
        {
            return observationContent;
        }

        var result = observationContent;

        try
        {
            result = RoleTokenPattern.Replace(
                result,
                static match =>
                {
                    // Wrap the token in brackets to break the injection semantic while
                    // keeping the content readable for the agent.
                    var token = match.Groups[1].Value;
                    var leadingWhitespace = match.Value[..match.Value.IndexOf(token, StringComparison.OrdinalIgnoreCase)];
                    return $"{leadingWhitespace}[{token}]:";
                }
            );

            result = XmlRoleTagPattern.Replace(result, static match => $"[{match.Value.Trim('<', '>', '/', ' ')}]");
        }
        catch (RegexMatchTimeoutException)
        {
            // On timeout, return the original content unchanged rather than
            // risking a crash. Log-worthy but not fatal.
            return observationContent;
        }

        return result;
    }
}
