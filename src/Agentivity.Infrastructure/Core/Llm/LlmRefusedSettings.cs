using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// The settings of a request a model may refuse while others take them — how the answer is sampled (<c>temperature</c>,
/// <c>top_p</c>, penalties), never what is asked. The latest Claude models answer 400 to a temperature ("deprecated for this
/// model"), OpenAI's reasoning models to <c>top_p</c> ("Unsupported parameter"). Whoever set it (a step, a person, the
/// platform), a setting the model refuses is not sent: the provider's own answer names it, the request goes again without it,
/// and it is remembered per address and model once the request without it went through — proof it was the cause, so a model
/// that refuses it pays the failed call once per process. Never a list of model names in code: a model that starts taking
/// the setting again is picked up at the next restart. Static because a client is created per call.
/// </summary>
internal static class LlmRefusedSettings
{
    private static readonly (string Name, Func<LlmConversationOptions, bool> IsSet, Func<LlmConversationOptions, LlmConversationOptions> Without)[] Settings =
    [
        ("temperature", o => o.Temperature is not null, o => o with { Temperature = null }),
        ("top_p", o => o.TopP is not null, o => o with { TopP = null }),
        ("frequency_penalty", o => o.FrequencyPenalty is not null, o => o with { FrequencyPenalty = null }),
        ("presence_penalty", o => o.PresencePenalty is not null, o => o with { PresencePenalty = null }),
    ];

    private static readonly ConcurrentDictionary<string, byte> Refused = new(StringComparer.Ordinal);

    // How a provider's 400 reads once described (see LlmProviderError.Describe).
    private static readonly string BadRequest = $"({HttpStatusCode.BadRequest})";

    private static string Key(string baseUrl, string model, string setting) => $"{baseUrl.TrimEnd('/')}|{model}|{setting}";

    /// <summary>
    /// Sends a request with <paramref name="send"/> — in whatever format the provider speaks — without the settings the model refuses: those
    /// it was found to refuse are left out from the start, and a refusal that names one sends the request again without it.
    /// </summary>
    /// <param name="address">Where the model is reached (an API address, an AWS region): a model is known per address.</param>
    public static async Task<OperationResult<LlmConversationResponse>> SendAsync(string address, string model, LlmConversationOptions options, Func<LlmConversationOptions, Task<OperationResult<LlmConversationResponse>>> send, ILogger logger)
    {
        var opts = WithoutRefused(address, model, options);
        var refused = new List<string>();
        while (true)
        {
            var result = await send(opts).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                foreach (var setting in refused)
                    Remember(address, model, setting);
                return result;
            }

            if (RefusedBy(result.Error, opts) is not { } refusedSetting)
                return result;

            logger.LogWarning("Model {Model} refuses the setting '{Setting}' ({Error}) — sending the request again without it. The generation of its API should leave it out: update it.", model, refusedSetting, result.Error);
            opts = Without(opts, refusedSetting);
            refused.Add(refusedSetting);
        }
    }

    /// <summary>The options without what this model, at this address, was found to refuse.</summary>
    public static LlmConversationOptions WithoutRefused(string baseUrl, string model, LlmConversationOptions options) =>
        Settings.Where(s => s.IsSet(options) && Refused.ContainsKey(Key(baseUrl, model, s.Name))).Aggregate(options, (o, s) => s.Without(o));

    /// <summary>
    /// The setting a failed request was refused for: the provider answered 400 and named, as a word, a setting the request
    /// carried. Null for any other failure — it says nothing about the settings.
    /// </summary>
    public static string? RefusedBy(string? error, LlmConversationOptions sent)
    {
        if (string.IsNullOrWhiteSpace(error) || !error.Contains(BadRequest, StringComparison.Ordinal))
            return null;
        return Settings.Where(s => s.IsSet(sent) && Regex.IsMatch(error, $@"\b{s.Name}\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250))).Select(s => s.Name).FirstOrDefault();
    }

    public static LlmConversationOptions Without(LlmConversationOptions options, string setting) => Settings.Single(s => s.Name == setting).Without(options);

    public static void Remember(string baseUrl, string model, string setting) => Refused[Key(baseUrl, model, setting)] = 0;
}
