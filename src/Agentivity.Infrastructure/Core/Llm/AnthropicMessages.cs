using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Anthropic's Messages API (<c>/v1/messages</c>), as every generation of Claude speaks it: the system prompt beside the
/// messages, tools, native structured outputs, the prompt cache. A generation (<see cref="AnthropicMessagesV1"/>,
/// <see cref="AnthropicMessagesV2"/>, <see cref="AnthropicMessagesV3"/>) names its models and says what they take. What a
/// model is found to refuse beyond that (a forced tool choice, a response schema) is still adapted to and remembered
/// (<see cref="AnthropicModelQuirks"/>) — a safety net, and a sign that a generation needs updating.
/// </summary>
public abstract class AnthropicMessages : LlmApiGeneration
{
    private const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// The models of this generation, as Anthropic names them (<c>claude-opus-4-7</c>). A name also covers its dated
    /// snapshots and its Bedrock and Google Cloud forms (<c>claude-sonnet-4-5-20250929</c>,
    /// <c>eu.anthropic.claude-sonnet-4-5-20250929-v1:0</c>, <c>claude-haiku-4-5@20251001</c>). A provider's client checks its
    /// generations newest first, so a newer generation claims its own models before an older one's shorter name does
    /// (<c>claude-opus-4-7</c> before <c>claude-opus-4</c>).
    /// </summary>
    protected abstract IReadOnlyList<string> ModelNames { get; }

    /// <summary>Whether the models take <c>temperature</c> and <c>top_p</c> (Claude 4.7 and later refuse a value other than the default).</summary>
    protected virtual bool TakesSamplingSettings => true;

    /// <summary>Whether the models take a forced tool choice (<c>tool_choice</c> <c>any</c> or <c>tool</c>).</summary>
    protected virtual bool TakesForcedToolChoice => true;

    public sealed override bool Covers(string model) => ModelNames.Any(name => Names(model, name));

    private static bool Names(string model, string name)
    {
        // Bedrock writes "anthropic.claude-…", with a region in front for its cross-region profiles ("eu.anthropic.claude-…").
        var id = model.ToLowerInvariant();
        var start = id.IndexOf("claude-", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        id = id[start..];
        return id == name || (id.StartsWith(name, StringComparison.Ordinal) && id[name.Length] is '-' or '@' or ':');
    }

    // A request adapts at most this many times (forced tool choice, strict tools, schema, firm reminder).
    private const int MaxAnthropicAttempts = 5;

    internal override async Task<OperationResult<LlmConversationResponse>> SendAsync(LlmApiCall call, CancellationToken cancellationToken)
    {
        var (model, messages, tools, options) = (call.Model, call.Messages, call.Tools, call.Options);
        var toolNames = call.ToolNames;
        if (!TakesSamplingSettings)
        {
            // Whoever set them (a step, a person, the platform): these models refuse them.
            options = options with { Temperature = null, TopP = null };
        }

        var forcesTool = options.ToolChoiceMode == LlmToolChoiceMode.Required && toolNames is not null;
        var hasSchema = !string.IsNullOrWhiteSpace(options.ResponseSchema);

        // Start from what this endpoint+model was already found to reject (see AnthropicModelQuirks), so a model that
        // refuses something costs one failed call per process — not one per call.
        var replaceForced = forcesTool && (!TakesForcedToolChoice || AnthropicModelQuirks.ForcedToolChoiceRejected(call.Address, model));
        var adaptations = new AnthropicRequestAdaptations(
            OmitResponseSchema: hasSchema && AnthropicModelQuirks.ResponseSchemaRejected(call.Address, model),
            ReplaceForcedToolChoice: replaceForced,
            StrictTools: replaceForced && AnyToolCanBeStrict(tools)
        );

        // What a failed call taught us is only remembered once a retry WORKED — proof the adaptation was the cause,
        // not some unrelated 400.
        var learned = new List<Action>();
        OperationResult<LlmConversationResponse> result = OperationResult<LlmConversationResponse>.Failure("Anthropic request was not sent.");

        for (var attempt = 0; attempt < MaxAnthropicAttempts; attempt++)
        {
            var payloadResult = BuildPayload(model, messages, tools, options, adaptations);
            if (payloadResult.IsFailure || payloadResult.Value is null)
            {
                return OperationResult<LlmConversationResponse>.Failure(payloadResult.Error ?? "Failed to build Anthropic payload.");
            }

            bool wasBadRequest;
            (result, wasBadRequest) = await PostAsync(call, payloadResult.Value, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Without a forced choice nothing makes the model call a tool: check, and ask once more, firmly.
                if (adaptations.ReplaceForcedToolChoice && !adaptations.FirmReminder && result.Value is { ToolCalls.Count: 0 })
                {
                    call.Logger.LogWarning("Model {Model} answered without the tool call that was required (forced tool choice is not available to it) — asking once more, more firmly.", model);
                    adaptations = adaptations with { FirmReminder = true };
                    continue;
                }

                foreach (var remember in learned)
                    remember();

                return result;
            }

            if (!wasBadRequest || LlmRefusedSettings.RefusedBy(result.Error, options) is not null)
                return result;

            // Forced tool use refused (the 400 says so, whatever the number of tools): ask for the call in words instead.
            if (forcesTool && !adaptations.ReplaceForcedToolChoice && AnthropicModelQuirks.IsForcedToolChoiceRejection(result.Error))
            {
                call.Logger.LogWarning("Anthropic model {Model} rejects a forced tool choice ({Error}) — asking for the tool call in the prompt instead (tool_choice auto).", model, result.Error);
                adaptations = adaptations with { ReplaceForcedToolChoice = true, StrictTools = AnyToolCanBeStrict(tools) };
                learned.Add(() => AnthropicModelQuirks.RememberForcedToolChoiceRejected(call.Address, model));
                continue;
            }

            // strict tools were only ever a bonus: if the API refuses them, drop them before blaming anything else.
            if (adaptations.StrictTools)
            {
                call.Logger.LogWarning("Anthropic rejected the request for model {Model} ({Error}) — retrying without strict tool use.", model, result.Error);
                adaptations = adaptations with { StrictTools = false };
                continue;
            }

            // A schema the API refuses must not turn a call that always worked into a failure: send it once more the
            // way it used to go out — schema left to the prompt — and say so.
            if (hasSchema && !adaptations.OmitResponseSchema)
            {
                call.Logger.LogWarning("Anthropic rejected the response schema for model {Model} ({Error}) — retrying without it; the answer is not schema-constrained.", model, result.Error);
                adaptations = adaptations with { OmitResponseSchema = true };
                learned.Add(() => AnthropicModelQuirks.RememberResponseSchemaRejected(call.Address, model));
                continue;
            }

            return result;
        }

        return result;
    }

    private static bool AnyToolCanBeStrict(IReadOnlyList<JsonObject>? tools) =>
        tools is { Count: > 0 } && tools.Any(tool => AnthropicResponseSchema.TryMakeStrict(LlmToolDefinitionJson.GetRequiredInputSchema(tool)) is not null);

    private static async Task<(OperationResult<LlmConversationResponse> Result, bool WasBadRequest)> PostAsync(LlmApiCall call, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["x-api-key"] = call.ApiKey, ["anthropic-version"] = AnthropicVersion };
        var reply = await call.Transport.SendAsync(new LlmWireRequest("Anthropic", "/v1/messages", payload, headers, call.Model), cancellationToken).ConfigureAwait(false);
        if (reply.Body.IsFailure || reply.Body.Value is null)
        {
            return (OperationResult<LlmConversationResponse>.Failure(reply.Body.Error ?? "Anthropic request failed."), reply.WasBadRequest);
        }

        return (ParseResponse(reply.Body.Value, call.ToolNames), false);
    }

    // Anthropic's Messages API requires max_tokens on every request — there is no "unlimited"
    // option, so this is the last-resort value used only when nothing upstream (AiNode, the
    // connected LLM Model node) supplied one. 2000 was confirmed too low in production —
    // truncating ordinary long-form output (e.g. a content-writer agent's draft) well before
    // the model itself would stop. 64000 matches Claude Haiku 4.5's own max output — the
    // smallest/baseline model in the lineup, so every other Claude model supports at least
    // this much, making it a safe reference ceiling rather than an arbitrary round number.
    private const int DefaultMaxTokens = 64000;

    /// <summary>The request body. Bedrock's Claude models take the same body, adjusted by its transport (see <see cref="BedrockLlmClient"/>).</summary>
    internal static OperationResult<Dictionary<string, object?>> BuildPayload(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options, AnthropicRequestAdaptations adaptations = default)
    {
        // Anthropic separates system prompt from the messages array.
        var systemMessage = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        var conversationMessages = messages.Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).Select(BuildMessagePayload).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = conversationMessages,
            ["max_tokens"] = options.MaxTokens ?? DefaultMaxTokens,
        };

        if (systemMessage is not null && !string.IsNullOrWhiteSpace(systemMessage.Content))
        {
            payload["system"] = systemMessage.Content;
        }

        if (options.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }

        if (options.TopP is double topP)
        {
            payload["top_p"] = topP;
        }

        // Native structured outputs. Without this the schema was silently dropped for Anthropic and the
        // only "structure" left was the prompt's "Return valid JSON only" — which a model answers with a
        // markdown fence and a paragraph of reasoning, and every caller that parses the reply strictly
        // then takes its default. Confirmed in production against claude-haiku-4-5.
        if (!string.IsNullOrWhiteSpace(options.ResponseSchema) && !adaptations.OmitResponseSchema)
        {
            var schemaResult = AnthropicResponseSchema.Build(options.ResponseSchema);
            if (schemaResult.IsFailure || schemaResult.Value is null)
            {
                return OperationResult<Dictionary<string, object?>>.Failure(schemaResult.Error ?? "Invalid response schema.");
            }

            payload["output_config"] = new Dictionary<string, object?>
            {
                ["format"] = new Dictionary<string, object?> { ["type"] = "json_schema", ["schema"] = schemaResult.Value },
            };
        }

        if (tools is { Count: > 0 })
        {
            var toolPayloads = new List<object>(tools.Count);
            foreach (var tool in tools)
            {
                var toolPayloadResult = BuildToolPayload(tool);
                if (toolPayloadResult.IsFailure || toolPayloadResult.Value is null)
                {
                    return OperationResult<Dictionary<string, object?>>.Failure(toolPayloadResult.Error ?? "Invalid tool schema.");
                }

                if (adaptations.StrictTools && toolPayloadResult.Value is Dictionary<string, object?> toolPayload && toolPayload["input_schema"] is JsonNode inputSchema && AnthropicResponseSchema.TryMakeStrict(inputSchema) is { } strictSchema)
                {
                    toolPayload["input_schema"] = strictSchema;
                    toolPayload["strict"] = true;
                }

                toolPayloads.Add(toolPayloadResult.Value);
            }

            payload["tools"] = toolPayloads;

            if (options.ToolChoiceMode == LlmToolChoiceMode.Required && adaptations.ReplaceForcedToolChoice)
            {
                // Some models (Claude Fable 5.1 / Mythos 5.1) refuse forced tool use outright. The documented
                // replacement: leave the choice to the model and state, in the last turn, that the call is required.
                payload["tool_choice"] = new Dictionary<string, object?> { ["type"] = "auto" };
                AppendToLastUserTurn(conversationMessages, BuildRequiredToolInstruction(tools.Select(LlmToolDefinitionJson.GetRequiredName).ToList(), adaptations.FirmReminder));
            }
            else
            {
                payload["tool_choice"] = MapToolChoice(options.ToolChoiceMode);
            }
        }

        ApplyPromptCache(payload, conversationMessages, tools, options);

        return OperationResult<Dictionary<string, object?>>.Success(payload);
    }

    /// <summary>
    /// Marks the stable start of the request for Anthropic's prompt cache: the instructions (which also covers the tools, that
    /// come first) and the last message (which covers the whole conversation so far). The next call of the same conversation
    /// finds both and pays about a tenth for what it re-sends. A request too small to be cached is simply not cached by the
    /// provider, so the marks are harmless there.
    /// </summary>
    internal static void ApplyPromptCache(Dictionary<string, object?> payload, List<Dictionary<string, object?>> conversationMessages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
    {
        var enabled = options.PromptCache switch
        {
            LlmPromptCacheMode.Off => false,
            LlmPromptCacheMode.On => true,
            _ => tools is { Count: > 0 } || conversationMessages.Count >= 3,
        };
        if (!enabled)
        {
            return;
        }

        static Dictionary<string, object?> Mark() => new() { ["type"] = "ephemeral" };

        if (payload.TryGetValue("system", out var system) && system is string systemText && systemText.Length > 0)
        {
            payload["system"] = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = systemText, ["cache_control"] = Mark() } };
        }

        if (conversationMessages.Count == 0)
        {
            return;
        }

        var last = conversationMessages[^1];
        switch (last["content"])
        {
            case string text when text.Length > 0:
                last["content"] = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text, ["cache_control"] = Mark() } };
                break;
            case List<object> blocks when blocks.Count > 0 && blocks[^1] is Dictionary<string, object?> lastBlock:
                lastBlock["cache_control"] = Mark();
                break;
        }
    }

    /// <summary>The words that stand in for a forced tool call: names the tool (or asks for one of them) and says it is required now.</summary>
    internal static string BuildRequiredToolInstruction(IReadOnlyList<string> toolNames, bool firm)
    {
        var target = toolNames.Count == 1 ? $"the `{toolNames[0]}` tool" : "one of the available tools";
        return firm
            ? $"You did not call a tool. Your response for this turn MUST be a call to {target} — call it now, with no other text."
            : $"Call {target} now: a tool call is required for this turn. Open your response with it.";
    }

    /// <summary>Adds `instruction` as a text block at the end of the last user turn (after any tool results) — or as a new user turn when the conversation does not end on one.</summary>
    private static void AppendToLastUserTurn(List<Dictionary<string, object?>> conversationMessages, string instruction)
    {
        var block = new Dictionary<string, object?> { ["type"] = "text", ["text"] = instruction };

        if (conversationMessages.Count > 0 && conversationMessages[^1] is { } last && string.Equals(last["role"] as string, "user", StringComparison.OrdinalIgnoreCase))
        {
            switch (last["content"])
            {
                case List<object> blocks:
                    blocks.Add(block);
                    return;
                case string text:
                    last["content"] = new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }, block };
                    return;
            }
        }

        conversationMessages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = new List<object> { block } });
    }

    private static Dictionary<string, object?> BuildMessagePayload(LlmChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Tool results go as a content block array with role "user".
        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            return new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Content ?? string.Empty,
                    },
                },
            };
        }

        // Assistant messages with tool calls become content blocks.
        if (message.ToolCalls is { Count: > 0 })
        {
            var blocks = new List<object>();

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Content });
            }

            foreach (var toolCall in message.ToolCalls)
            {
                var input = TryParseJsonObject(toolCall.ArgumentsJson) ?? new Dictionary<string, object?>();
                blocks.Add(
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = toolCall.Name,
                        ["input"] = input,
                    }
                );
            }

            return new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = blocks };
        }

        if (message.Images is { Count: > 0 })
        {
            var blocks = new List<object>();

            // Images before text is Anthropic's own recommendation for best comprehension.
            foreach (var image in message.Images)
            {
                blocks.Add(new Dictionary<string, object?> { ["type"] = "image", ["source"] = new Dictionary<string, object?> { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.Base64Data } });
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Content });
            }

            return new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = blocks };
        }

        return new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content ?? string.Empty };
    }

    private static OperationResult<object> BuildToolPayload(JsonObject tool)
    {
        try
        {
            var name = LlmToolDefinitionJson.GetRequiredName(tool);
            var inputSchema = LlmToolDefinitionJson.GetRequiredInputSchema(tool).DeepClone();
            var description = LlmToolDefinitionJson.GetDescription(tool);

            return OperationResult<object>.Success(
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["input_schema"] = inputSchema,
                }
            );
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return OperationResult<object>.Failure($"Invalid tool definition: {ex.Message}");
        }
    }

    private static object MapToolChoice(LlmToolChoiceMode mode) =>
        mode switch
        {
            LlmToolChoiceMode.None => new Dictionary<string, object?> { ["type"] = "none" },
            LlmToolChoiceMode.Required => new Dictionary<string, object?> { ["type"] = "any" },
            _ => new Dictionary<string, object?> { ["type"] = "auto" },
        };

    /// <summary>The answer. Bedrock answers a Claude model with the same JSON as the direct API.</summary>
    internal static OperationResult<LlmConversationResponse> ParseResponse(string responseJson, IReadOnlyList<string>? toolNames)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Anthropic error envelope: { "type": "error", "error": { "message": "..." } }
            if (root.TryGetProperty("type", out var typeEl) && string.Equals(typeEl.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                var errMsg = root.TryGetProperty("error", out var errEl) && errEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "Unknown Anthropic error." : "Unknown Anthropic error.";
                return OperationResult<LlmConversationResponse>.Failure(errMsg);
            }

            if (!root.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                return OperationResult<LlmConversationResponse>.Failure("Anthropic response did not contain a content array.");
            }

            var textParts = new List<string>();
            var toolCalls = new List<LlmToolCall>();

            foreach (var block in contentArray.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockType))
                {
                    continue;
                }

                var blockTypeStr = blockType.GetString();

                if (string.Equals(blockTypeStr, "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            textParts.Add(text);
                        }
                    }
                }
                else if (string.Equals(blockTypeStr, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    if (!block.TryGetProperty("id", out var idEl) || !block.TryGetProperty("name", out var nameEl))
                    {
                        continue;
                    }

                    var argumentsJson = block.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : "{}";

                    toolCalls.Add(new LlmToolCall(idEl.GetString() ?? Guid.NewGuid().ToString("N"), nameEl.GetString() ?? string.Empty, argumentsJson));
                }
            }

            var content = textParts.Count > 0 ? string.Join(Environment.NewLine, textParts) : null;

            if (toolCalls.Count == 0)
            {
                var extracted = ExtractToolCallsFromContent(content, out var sanitizedContent);
                if (extracted.Count > 0)
                {
                    toolCalls.AddRange(extracted);
                    content = sanitizedContent;
                }
            }

            if (toolCalls.Count == 0 && ContainsTextualToolInvocation(content, toolNames))
            {
                return OperationResult<LlmConversationResponse>.Failure("Anthropic response contained a textual tool invocation instead of a native tool call.");
            }

            var stopReason = root.TryGetProperty("stop_reason", out var stopEl) ? stopEl.GetString() ?? string.Empty : string.Empty;

            var tokensUsed = 0;
            var cacheRead = 0;
            var cacheWrite = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                var inputTokens = usage.TryGetProperty("input_tokens", out var inTok) ? inTok.GetInt32() : 0;
                var outputTokens = usage.TryGetProperty("output_tokens", out var outTok) ? outTok.GetInt32() : 0;

                // With the prompt cache, input_tokens counts only what was not served from it: the whole input is the three.
                cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var readTok) && readTok.ValueKind == JsonValueKind.Number ? readTok.GetInt32() : 0;
                cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var writeTok) && writeTok.ValueKind == JsonValueKind.Number ? writeTok.GetInt32() : 0;
                tokensUsed = inputTokens + outputTokens + cacheRead + cacheWrite;
            }

            return OperationResult<LlmConversationResponse>.Success(new LlmConversationResponse(content, tokensUsed, stopReason, toolCalls, null, CachedInputTokens: cacheRead, CacheWriteTokens: cacheWrite));
        }
        catch (JsonException ex)
        {
            return OperationResult<LlmConversationResponse>.Failure($"Failed to parse Anthropic response: {ex.Message}");
        }
    }
}
