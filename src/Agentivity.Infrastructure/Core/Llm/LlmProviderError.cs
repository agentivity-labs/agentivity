using System.Net;
using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Turns what an LLM provider answered when it refused a request into an error the end user can act on. A run that stops
/// because the account is out of credit, the API key was rejected or the provider is overloaded must say so — plainly, and
/// with a stable code the clients can key on — rather than a generic "agent step failed".
/// <para>
/// The text follows the platform's <c>[code] message</c> convention (see <c>tool_call_failed</c>): the code travels with the
/// message through run errors and AG-UI <c>RUN_ERROR</c> events, where it is split off again (<see cref="TrySplitCode"/>).
/// A refusal that is none of the known kinds keeps the provider's own words, unchanged and without a code.
/// </para>
/// </summary>
public static class LlmProviderError
{
    /// <summary>The account is out of credit, over its spending limit or has no active billing.</summary>
    public const string Billing = "llm_billing";

    /// <summary>The API key is missing, invalid, revoked or not allowed to use the model.</summary>
    public const string Authentication = "llm_auth";

    /// <summary>Too many requests, or a usage-per-minute / per-day limit reached — usually clears by waiting.</summary>
    public const string RateLimited = "llm_rate_limited";

    /// <summary>The provider is overloaded or down.</summary>
    public const string Unavailable = "llm_unavailable";

    private static readonly Regex BillingText = new(
        @"credit balance|insufficient[_ ]quota|insufficient[_ ]funds|out of credits?|billing|payment required|exceeded your current quota|spend(ing)? limit|usage limit.*(reached|exceeded)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250)
    );

    private static readonly Regex AuthText = new(
        @"invalid (x-)?api[ _-]?key|api key not valid|incorrect api key|authentication|unauthori[sz]ed|permission denied|not allowed to use",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250)
    );

    /// <summary>
    /// The failure message for a provider refusal: <c>[code] explanation (provider says: …)</c> for a known kind, otherwise
    /// <c>"{provider} API error ({status}): {providerMessage}"</c> as before.
    /// </summary>
    /// <param name="provider">Display name of the provider ("Anthropic", "Gemini", …).</param>
    /// <param name="status">The HTTP status the provider answered.</param>
    /// <param name="providerMessage">The provider's own error message, already extracted from its response.</param>
    public static string Describe(string provider, HttpStatusCode status, string providerMessage)
    {
        var plain = $"{provider} API error ({status}): {providerMessage}";
        var code = Classify(status, providerMessage);
        if (code is null)
            return plain;

        var explanation = code switch
        {
            Billing => $"The AI service ({provider}) refused the request because the account is out of credit or has no active billing. Add credit in {provider}'s billing settings, then try again.",
            Authentication => $"The AI service ({provider}) rejected the API key. Check that the key configured for this model is valid and allowed to use it, then try again.",
            RateLimited => $"The AI service ({provider}) is receiving too many requests or has reached a usage limit. Wait a moment, then try again.",
            _ => $"The AI service ({provider}) is overloaded or temporarily unavailable. Try again in a few minutes.",
        };
        return $"[{code}] {explanation} ({provider} says: {providerMessage})";
    }

    /// <summary>The code for a refusal, or null when it is none of the known kinds.</summary>
    public static string? Classify(HttpStatusCode status, string providerMessage)
    {
        var code = (int)status;
        var text = providerMessage ?? string.Empty;
        if (code == 402 || SafeMatch(BillingText, text))
            return Billing;
        if (code is 401 or 403 || SafeMatch(AuthText, text))
            return Authentication;
        if (code == 429)
            return RateLimited;
        if (code is 500 or 502 or 503 or 504 or 529 || text.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return Unavailable;
        return null;
    }

    /// <summary>
    /// Splits a leading <c>[code]</c> off an error message — the code is lower-case words joined by underscores, which tells it from
    /// a member's display name in <c>[Needs Analyst] …</c>. Returns false when the message carries no code.
    /// </summary>
    public static bool TrySplitCode(string? message, out string code, out string text)
    {
        code = string.Empty;
        text = message ?? string.Empty;
        if (string.IsNullOrEmpty(message) || message[0] != '[')
            return false;
        var end = message.IndexOf(']');
        if (end < 2)
            return false;
        var candidate = message[1..end];
        foreach (var ch in candidate)
        {
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                return false;
        }

        code = candidate;
        text = message[(end + 1)..].TrimStart();
        return true;
    }

    private static bool SafeMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
