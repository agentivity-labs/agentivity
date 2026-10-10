using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>
/// Looks for what must never travel in a Template: API keys and tokens of the usual providers, private keys, and any value
/// stored in one of the user's own credentials. Same shapes as the pipeline scanner (<c>tools/build/scan-secrets.mjs</c>).
/// Only the kind of secret is ever reported, never its value.
/// </summary>
public static partial class TemplateSecretScanner
{
    private static readonly (string Kind, Regex Pattern)[] Detectors =
    [
        ("Brevo API key", BrevoKey()),
        ("Anthropic API key", AnthropicKey()),
        ("OpenAI API key", OpenAiKey()),
        ("Stripe live key", StripeKey()),
        ("Stripe webhook secret", StripeWebhook()),
        ("AWS access key id", AwsKey()),
        ("GitHub token", GitHubToken()),
        ("Google API key", GoogleKey()),
        ("Slack token", SlackToken()),
        ("SendGrid API key", SendGridKey()),
        ("Private key", PrivateKey()),
    ];

    // Obvious placeholders are not secrets.
    [GeneratedRegex(@"(x{6,}|0{8,}|\.{3}|<[^>]+>|example|your[-_ ]?(?:api[-_ ]?)?key|placeholder|dummy|changeme|redacted)", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"xkeysib-[a-f0-9]{40,}")]
    private static partial Regex BrevoKey();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{20,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\b[sr]k_live_[A-Za-z0-9]{16,}")]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bwhsec_[A-Za-z0-9]{24,}")]
    private static partial Regex StripeWebhook();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bSG\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}")]
    private static partial Regex SendGridKey();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY")]
    private static partial Regex PrivateKey();

    /// <summary>The kinds of secret found in <paramref name="text"/> (each kind once).</summary>
    /// <param name="ownSecrets">Values stored in the user's credentials: any of them appearing in the text is a leak, whatever its shape.</param>
    public static IReadOnlyList<string> Scan(string text, IEnumerable<string>? ownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kinds = new List<string>();

        foreach (var (kind, pattern) in Detectors)
        {
            if (pattern.Matches(text).Any(m => !Placeholder().IsMatch(m.Value)))
            {
                kinds.Add(kind);
            }
        }

        foreach (var secret in ownSecrets ?? [])
        {
            // Short values (a region name, "true") would match everywhere: only values that look like real secrets count.
            if (secret.Length >= 12 && text.Contains(secret, StringComparison.Ordinal))
            {
                kinds.Add("A value stored in one of your credentials");
                break;
            }
        }

        return kinds;
    }
}
