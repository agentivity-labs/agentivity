using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// What one Anthropic request is adapted to, as learned from the API rather than assumed from a model name.
/// The default value is the request exactly as the platform builds it.
/// </summary>
/// <param name="OmitResponseSchema">Leave the response schema to the prompt (the model has no structured outputs, or the schema cannot be expressed).</param>
/// <param name="ReplaceForcedToolChoice">
/// Send <c>tool_choice: auto</c> plus an explicit instruction instead of the forced <c>any</c> — for models
/// that reject forced tool use (Claude Fable 5.1 / Mythos 5.1 answer 400 to it, however many tools there are).
/// </param>
/// <param name="StrictTools">Ask for schema-valid tool arguments (<c>strict: true</c>) where a tool's schema allows it — what forced tool use used to guarantee.</param>
/// <param name="FirmReminder">The model answered without any tool call: say, more firmly, that one is required.</param>
internal readonly record struct AnthropicRequestAdaptations(bool OmitResponseSchema = false, bool ReplaceForcedToolChoice = false, bool StrictTools = false, bool FirmReminder = false);

/// <summary>
/// Remembers, per endpoint and model, what an Anthropic model was found to reject — so that a model that
/// refuses something pays the failed call once per process, not on every call. Keyed by endpoint AND model
/// (never by model name alone, never a list of names in code): the API's own answer is the source of truth,
/// so a model that gains support is picked up at the next restart. Static because the client is created per
/// call — an instance-level memory would always be empty.
/// </summary>
internal static class AnthropicModelQuirks
{
    private static readonly ConcurrentDictionary<string, byte> RejectsForcedToolChoice = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> RejectsResponseSchema = new(StringComparer.Ordinal);

    private static string Key(string baseUrl, string model) => $"{baseUrl.TrimEnd('/')}|{model}";

    public static bool ForcedToolChoiceRejected(string baseUrl, string model) => RejectsForcedToolChoice.ContainsKey(Key(baseUrl, model));

    public static void RememberForcedToolChoiceRejected(string baseUrl, string model) => RejectsForcedToolChoice[Key(baseUrl, model)] = 0;

    public static bool ResponseSchemaRejected(string baseUrl, string model) => RejectsResponseSchema.ContainsKey(Key(baseUrl, model));

    public static void RememberResponseSchemaRejected(string baseUrl, string model) => RejectsResponseSchema[Key(baseUrl, model)] = 0;

    /// <summary>
    /// True when a 400's message is the API refusing a forced <c>tool_choice</c> (<c>any</c> / <c>tool</c>) — the only
    /// 400 that justifies changing how the tool call is requested. Any other 400 says nothing about it.
    /// </summary>
    public static bool IsForcedToolChoiceRejection(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Contains("tool_choice", StringComparison.OrdinalIgnoreCase)
        && error.Contains("not supported", StringComparison.OrdinalIgnoreCase);
}
