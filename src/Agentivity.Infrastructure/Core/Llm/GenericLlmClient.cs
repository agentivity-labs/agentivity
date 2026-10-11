using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Domain;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Generic LLM client supporting OpenAI, Anthropic, and Gemini wire formats.
/// The <see cref="LlmApiFlavor"/> is passed explicitly by the provider node —
/// no URL auto-detection is performed.
/// </summary>
public sealed class GenericLlmClient : ILlmClient
{
    private static readonly TimeSpan LlmRequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly Regex ToolCallMarkupRegex = new(@"<tool_call>\s*(.*?)\s*</tool_call>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex TextualToolInvocationRegex = new(@"```[\s\S]*?[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string AnthropicVersion = "2023-06-01";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly ILogger _logger;
    private readonly LlmApiFlavor _flavor;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public GenericLlmClient(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, string? defaultModel, ILogger logger, LlmApiFlavor flavor = LlmApiFlavor.OpenAI)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "gpt-4" : defaultModel;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _flavor = flavor;
    }

    public Task<OperationResult<LlmResponse>> CompleteAsync(string prompt, string? systemPrompt = null, double temperature = 0.7, int? maxTokens = null, string? responseSchema = null, string? modelOverride = null, int? contextLength = null, CancellationToken cancellationToken = default) =>
        CompleteAsync(prompt, images: [], systemPrompt, temperature, maxTokens, responseSchema, modelOverride, contextLength, cancellationToken);

    public async Task<OperationResult<LlmResponse>> CompleteAsync(string prompt, IReadOnlyList<ImageAttachment> images, string? systemPrompt = null, double temperature = 0.7, int? maxTokens = null, string? responseSchema = null, string? modelOverride = null, int? contextLength = null, CancellationToken cancellationToken = default)
    {
        var messages = new List<LlmChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new LlmChatMessage("system", systemPrompt));
        }

        messages.Add(new LlmChatMessage("user", prompt, Images: images.Count > 0 ? images : null));

        var conversationResult = await CompleteConversationAsync(messages, tools: null, options: new LlmConversationOptions(Temperature: temperature, MaxTokens: maxTokens, ResponseSchema: responseSchema, ContextLength: contextLength), modelOverride, cancellationToken).ConfigureAwait(false);

        if (conversationResult.IsFailure)
        {
            return OperationResult<LlmResponse>.Failure(conversationResult.Error ?? "LLM completion failed.");
        }

        var response = conversationResult.Value;
        if (response is null || string.IsNullOrWhiteSpace(response.Content))
        {
            return OperationResult<LlmResponse>.Failure("LLM response did not contain content.");
        }

        return OperationResult<LlmResponse>.Success(new LlmResponse(response.Content, response.TokensUsed));
    }

    public async Task<OperationResult<LlmConversationResponse>> CompleteConversationAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools = null, LlmConversationOptions? options = null, string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return OperationResult<LlmConversationResponse>.Failure("At least one chat message is required.");
        }

        var model = modelOverride ?? _defaultModel;
        var opts = options ?? new LlmConversationOptions();

        OperationResult<LlmConversationResponse> result;
        if (_flavor == LlmApiFlavor.Anthropic)
        {
            result = await SendAnthropicConversationRequestAsync(model, messages, tools, opts, cancellationToken).ConfigureAwait(false);
        }
        else if (_flavor == LlmApiFlavor.Gemini)
        {
            result = await SendGeminiConversationRequestAsync(model, messages, tools, opts, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var payloadResult = BuildConversationPayload(model, messages, tools, opts);
            if (payloadResult.IsFailure || payloadResult.Value is null)
            {
                return OperationResult<LlmConversationResponse>.Failure(payloadResult.Error ?? "Failed to build LLM payload.");
            }

            var toolNames = tools is { Count: > 0 } ? tools.Select(LlmToolDefinitionJson.GetRequiredName).ToList() : null;
            result = await SendConversationRequestAsync(payloadResult.Value, toolNames, cancellationToken).ConfigureAwait(false);
        }

        // A truncated call is a deviation from what the caller asked for (it set max_tokens
        // expecting the whole answer to fit) — never let it pass through as a silent Success that
        // nobody happens to check FinishReason on. See Agentivity.Domain.LlmCallException.
        if (result.IsSuccess && result.Value is { } response && IsTruncatedFinishReason(response.FinishReason))
        {
            throw new LlmCallException(
                code: "llm_truncated",
                message: $"LLM call was cut off by its max_tokens limit (finish_reason='{response.FinishReason}').",
                provider: _flavor.ToString(), model: model, finishReason: response.FinishReason);
        }

        return result;
    }

    /// <summary>Widened from private so <see cref="BedrockLlmClient"/> can apply the same
    /// truncated-response guard for Anthropic-on-Bedrock as every other flavor gets here.</summary>
    internal static bool IsTruncatedFinishReason(string? finishReason) =>
        finishReason is not null && (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase) || finishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // Anthropic Messages API
    // -------------------------------------------------------------------------

    // A request adapts at most this many times (forced tool choice, strict tools, schema, firm reminder).
    private const int MaxAnthropicAttempts = 5;

    private async Task<OperationResult<LlmConversationResponse>> SendAnthropicConversationRequestAsync(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options, CancellationToken cancellationToken)
    {
        var toolNames = tools is { Count: > 0 } ? tools.Select(LlmToolDefinitionJson.GetRequiredName).ToList() : null;
        var forcesTool = options.ToolChoiceMode == LlmToolChoiceMode.Required && toolNames is not null;
        var hasSchema = !string.IsNullOrWhiteSpace(options.ResponseSchema);

        // Start from what this endpoint+model was already found to reject (see AnthropicModelQuirks), so a model that
        // refuses something costs one failed call per process — not one per call.
        var replaceForced = forcesTool && AnthropicModelQuirks.ForcedToolChoiceRejected(_baseUrl, model);
        var adaptations = new AnthropicRequestAdaptations(
            OmitResponseSchema: hasSchema && AnthropicModelQuirks.ResponseSchemaRejected(_baseUrl, model),
            ReplaceForcedToolChoice: replaceForced,
            StrictTools: replaceForced && AnyToolCanBeStrict(tools)
        );

        // What a failed call taught us is only remembered once a retry WORKED — proof the adaptation was the cause,
        // not some unrelated 400.
        var learned = new List<Action>();
        OperationResult<LlmConversationResponse> result = OperationResult<LlmConversationResponse>.Failure("Anthropic request was not sent.");

        for (var attempt = 0; attempt < MaxAnthropicAttempts; attempt++)
        {
            var payloadResult = BuildAnthropicPayload(model, messages, tools, options, adaptations);
            if (payloadResult.IsFailure || payloadResult.Value is null)
            {
                return OperationResult<LlmConversationResponse>.Failure(payloadResult.Error ?? "Failed to build Anthropic payload.");
            }

            bool wasBadRequest;
            (result, wasBadRequest) = await PostAnthropicAsync(payloadResult.Value, toolNames, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                // Without a forced choice nothing makes the model call a tool: check, and ask once more, firmly.
                if (adaptations.ReplaceForcedToolChoice && !adaptations.FirmReminder && result.Value is { ToolCalls.Count: 0 })
                {
                    _logger.LogWarning("Model {Model} answered without the tool call that was required (forced tool choice is not available to it) — asking once more, more firmly.", model);
                    adaptations = adaptations with { FirmReminder = true };
                    continue;
                }

                foreach (var remember in learned)
                    remember();

                return result;
            }

            if (!wasBadRequest)
                return result;

            // Forced tool use refused (the 400 says so, whatever the number of tools): ask for the call in words instead.
            if (forcesTool && !adaptations.ReplaceForcedToolChoice && AnthropicModelQuirks.IsForcedToolChoiceRejection(result.Error))
            {
                _logger.LogWarning("Anthropic model {Model} rejects a forced tool choice ({Error}) — asking for the tool call in the prompt instead (tool_choice auto).", model, result.Error);
                adaptations = adaptations with { ReplaceForcedToolChoice = true, StrictTools = AnyToolCanBeStrict(tools) };
                learned.Add(() => AnthropicModelQuirks.RememberForcedToolChoiceRejected(_baseUrl, model));
                continue;
            }

            // strict tools were only ever a bonus: if the API refuses them, drop them before blaming anything else.
            if (adaptations.StrictTools)
            {
                _logger.LogWarning("Anthropic rejected the request for model {Model} ({Error}) — retrying without strict tool use.", model, result.Error);
                adaptations = adaptations with { StrictTools = false };
                continue;
            }

            // A schema the API refuses must not turn a call that always worked into a failure: send it once more the
            // way it used to go out — schema left to the prompt — and say so.
            if (hasSchema && !adaptations.OmitResponseSchema)
            {
                _logger.LogWarning("Anthropic rejected the response schema for model {Model} ({Error}) — retrying without it; the answer is not schema-constrained.", model, result.Error);
                adaptations = adaptations with { OmitResponseSchema = true };
                learned.Add(() => AnthropicModelQuirks.RememberResponseSchemaRejected(_baseUrl, model));
                continue;
            }

            return result;
        }

        return result;
    }

    private static bool AnyToolCanBeStrict(IReadOnlyList<JsonObject>? tools) =>
        tools is { Count: > 0 } && tools.Any(tool => AnthropicResponseSchema.TryMakeStrict(LlmToolDefinitionJson.GetRequiredInputSchema(tool)) is not null);

    private async Task<(OperationResult<LlmConversationResponse> Result, bool WasBadRequest)> PostAnthropicAsync(Dictionary<string, object?> payload, IReadOnlyList<string>? toolNames, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = LlmRequestTimeout;
        var endpoint = $"{_baseUrl}/v1/messages";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", AnthropicVersion);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var requestJson = JsonSerializer.Serialize(payload, SerializerOptions);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        _logger.LogDebug("[LLM][Anthropic] --> {Endpoint} chars={Chars}", endpoint, requestJson.Length);
        _logger.LogDebug("[LLM][Anthropic] --> payload{NewLine}{Payload}", Environment.NewLine, requestJson);

        try
        {
            using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Anthropic API error: {StatusCode} {Body}", httpResponse.StatusCode, responseBody);
                return (OperationResult<LlmConversationResponse>.Failure(LlmProviderError.Describe("Anthropic", httpResponse.StatusCode, ExtractErrorMessage(responseBody))), httpResponse.StatusCode == System.Net.HttpStatusCode.BadRequest);
            }

            TryExtractUsage(responseBody, out var promptTokens, out var completionTokens, out var totalTokens);
            _logger.LogDebug("[LLM][Anthropic] <-- {StatusCode} prompt_tokens={PromptTokens} completion_tokens={CompletionTokens} total_tokens={TotalTokens}", (int)httpResponse.StatusCode, promptTokens, completionTokens, totalTokens);
            _logger.LogDebug("[LLM][Anthropic] <-- response{NewLine}{Body}", Environment.NewLine, responseBody);

            return (ParseAnthropicResponse(responseBody, toolNames), false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Anthropic API request failed");
            return (OperationResult<LlmConversationResponse>.Failure($"Anthropic request failed: {ex.Message}"), false);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Anthropic API request was canceled by the caller");
            return (OperationResult<LlmConversationResponse>.Failure("Anthropic request was canceled before completion."), false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Anthropic API request timed out after {Timeout}", LlmRequestTimeout);
            return (OperationResult<LlmConversationResponse>.Failure($"Anthropic request timed out after {LlmRequestTimeout.TotalSeconds}s."), false);
        }
    }

    // Anthropic's Messages API requires max_tokens on every request — there is no "unlimited"
    // option, so this is the last-resort value used only when nothing upstream (AiNode, the
    // connected LLM Model node) supplied one. 2000 was confirmed too low in production —
    // truncating ordinary long-form output (e.g. a content-writer agent's draft) well before
    // the model itself would stop. 64000 matches Claude Haiku 4.5's own max output — the
    // smallest/baseline model in the lineup, so every other Claude model supports at least
    // this much, making it a safe reference ceiling rather than an arbitrary round number.
    private const int DefaultMaxTokens = 64000;

    /// <summary>Widened from private so <see cref="BedrockLlmClient"/> can reuse the exact same
    /// Anthropic Messages payload — Bedrock's Anthropic models accept the identical body, minus
    /// the top-level "model" field (specified via the Bedrock modelId instead) plus an added
    /// "anthropic_version" field (see BedrockLlmClient), which the caller applies after this
    /// returns rather than duplicating this whole method.</summary>
    internal static OperationResult<Dictionary<string, object?>> BuildAnthropicPayload(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options, AnthropicRequestAdaptations adaptations = default)
    {
        // Anthropic separates system prompt from the messages array.
        var systemMessage = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        var conversationMessages = messages.Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).Select(BuildAnthropicMessagePayload).ToList();

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
                var toolPayloadResult = BuildAnthropicToolPayload(tool);
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
                payload["tool_choice"] = MapAnthropicToolChoice(options.ToolChoiceMode);
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

    private static Dictionary<string, object?> BuildAnthropicMessagePayload(LlmChatMessage message)
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

    private static OperationResult<object> BuildAnthropicToolPayload(JsonObject tool)
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

    private static object MapAnthropicToolChoice(LlmToolChoiceMode mode) =>
        mode switch
        {
            LlmToolChoiceMode.None => new Dictionary<string, object?> { ["type"] = "none" },
            LlmToolChoiceMode.Required => new Dictionary<string, object?> { ["type"] = "any" },
            _ => new Dictionary<string, object?> { ["type"] = "auto" },
        };

    /// <summary>Widened from private so <see cref="BedrockLlmClient"/> can reuse it — Bedrock's
    /// InvokeModel response body for an Anthropic model is byte-for-byte the same Anthropic
    /// Messages response JSON as the native API returns.</summary>
    internal static OperationResult<LlmConversationResponse> ParseAnthropicResponse(string responseJson, IReadOnlyList<string>? toolNames)
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

    private static object? TryParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Google Gemini API  (generativelanguage.googleapis.com)
    // -------------------------------------------------------------------------

    private async Task<OperationResult<LlmConversationResponse>> SendGeminiConversationRequestAsync(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options, CancellationToken cancellationToken)
    {
        var payloadResult = BuildGeminiPayload(messages, tools, options);
        if (payloadResult.IsFailure || payloadResult.Value is null)
        {
            return OperationResult<LlmConversationResponse>.Failure(payloadResult.Error ?? "Failed to build Gemini payload.");
        }

        var toolNames = tools is { Count: > 0 } ? tools.Select(LlmToolDefinitionJson.GetRequiredName).ToList() : null;

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = LlmRequestTimeout;

        // Gemini endpoint: POST /v1beta/models/{model}:generateContent?key={apiKey}
        var endpoint = $"{_baseUrl}/models/{model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var requestJson = JsonSerializer.Serialize(payloadResult.Value, SerializerOptions);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        _logger.LogDebug("[LLM][Gemini] --> {Endpoint} chars={Chars}", endpoint, requestJson.Length);
        _logger.LogDebug("[LLM][Gemini] --> payload{NewLine}{Payload}", Environment.NewLine, requestJson);

        try
        {
            using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini API error: {StatusCode} {Body}", httpResponse.StatusCode, responseBody);
                return OperationResult<LlmConversationResponse>.Failure(LlmProviderError.Describe("Gemini", httpResponse.StatusCode, ExtractGeminiErrorMessage(responseBody)));
            }

            _logger.LogDebug("[LLM][Gemini] <-- {StatusCode}", (int)httpResponse.StatusCode);
            _logger.LogDebug("[LLM][Gemini] <-- response{NewLine}{Body}", Environment.NewLine, responseBody);

            return ParseGeminiResponse(responseBody, toolNames);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini API request failed");
            return OperationResult<LlmConversationResponse>.Failure($"Gemini request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Gemini API request was canceled by the caller");
            return OperationResult<LlmConversationResponse>.Failure("Gemini request was canceled before completion.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Gemini API request timed out after {Timeout}", LlmRequestTimeout);
            return OperationResult<LlmConversationResponse>.Failure($"Gemini request timed out after {LlmRequestTimeout.TotalSeconds}s.");
        }
    }

    private static OperationResult<Dictionary<string, object?>> BuildGeminiPayload(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
    {
        // Gemini separates system instruction from the conversation contents.
        var systemMessage = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        var conversationMessages = messages.Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).Select(BuildGeminiContentPayload).ToList();

        var payload = new Dictionary<string, object?> { ["contents"] = conversationMessages };

        if (systemMessage is not null && !string.IsNullOrWhiteSpace(systemMessage.Content))
        {
            payload["system_instruction"] = new Dictionary<string, object?> { ["parts"] = new List<object> { new Dictionary<string, object?> { ["text"] = systemMessage.Content } } };
        }

        var generationConfig = new Dictionary<string, object?>();
        if (options.Temperature is double temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        if (options.TopP is double topP)
        {
            generationConfig["top_p"] = topP;
        }

        if (options.MaxTokens is int maxTokens)
        {
            generationConfig["max_output_tokens"] = maxTokens;
        }

        if (!string.IsNullOrWhiteSpace(options.ResponseSchema))
        {
            generationConfig["response_mime_type"] = "application/json";
            try
            {
                generationConfig["response_schema"] = JsonNode.Parse(options.ResponseSchema);
            }
            catch (JsonException ex)
            {
                return OperationResult<Dictionary<string, object?>>.Failure($"Invalid response schema: {ex.Message}");
            }
        }

        if (generationConfig.Count > 0)
        {
            payload["generation_config"] = generationConfig;
        }

        if (tools is { Count: > 0 })
        {
            var functionDeclarations = new List<object>(tools.Count);
            foreach (var tool in tools)
            {
                var toolResult = BuildGeminiFunctionDeclaration(tool);
                if (toolResult.IsFailure || toolResult.Value is null)
                {
                    return OperationResult<Dictionary<string, object?>>.Failure(toolResult.Error ?? "Invalid tool schema.");
                }

                functionDeclarations.Add(toolResult.Value);
            }

            payload["tools"] = new List<object> { new Dictionary<string, object?> { ["function_declarations"] = functionDeclarations } };

            payload["tool_config"] = options.ToolChoiceMode switch
            {
                LlmToolChoiceMode.None => new Dictionary<string, object?> { ["function_calling_config"] = new Dictionary<string, object?> { ["mode"] = "NONE" } },
                LlmToolChoiceMode.Required => new Dictionary<string, object?> { ["function_calling_config"] = new Dictionary<string, object?> { ["mode"] = "ANY" } },
                _ => new Dictionary<string, object?> { ["function_calling_config"] = new Dictionary<string, object?> { ["mode"] = "AUTO" } },
            };
        }

        return OperationResult<Dictionary<string, object?>>.Success(payload);
    }

    private static Dictionary<string, object?> BuildGeminiContentPayload(LlmChatMessage message)
    {
        // Gemini uses "model" for assistant role, "user" for user/tool results.
        var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
        var parts = new List<object>();

        // Tool result message → functionResponse part
        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            var responseContent = TryParseJsonObject(message.Content ?? "{}") ?? new Dictionary<string, object?> { ["output"] = message.Content };
            parts.Add(
                new Dictionary<string, object?>
                {
                    ["function_response"] = new Dictionary<string, object?> { ["name"] = message.ToolCallId, ["response"] = responseContent },
                }
            );

            return new Dictionary<string, object?> { ["role"] = "user", ["parts"] = parts };
        }

        // Assistant message with tool calls → functionCall parts
        if (message.ToolCalls is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                parts.Add(new Dictionary<string, object?> { ["text"] = message.Content });
            }

            foreach (var toolCall in message.ToolCalls)
            {
                var args = TryParseJsonObject(toolCall.ArgumentsJson) ?? new Dictionary<string, object?>();
                parts.Add(
                    new Dictionary<string, object?>
                    {
                        ["function_call"] = new Dictionary<string, object?> { ["name"] = toolCall.Name, ["args"] = args },
                    }
                );
            }

            return new Dictionary<string, object?> { ["role"] = "model", ["parts"] = parts };
        }

        parts.Add(new Dictionary<string, object?> { ["text"] = message.Content ?? string.Empty });

        if (message.Images is { Count: > 0 })
        {
            foreach (var image in message.Images)
            {
                parts.Add(new Dictionary<string, object?> { ["inline_data"] = new Dictionary<string, object?> { ["mime_type"] = image.MimeType, ["data"] = image.Base64Data } });
            }
        }

        return new Dictionary<string, object?> { ["role"] = role, ["parts"] = parts };
    }

    private static OperationResult<object> BuildGeminiFunctionDeclaration(JsonObject tool)
    {
        try
        {
            var name = LlmToolDefinitionJson.GetRequiredName(tool);
            var parameters = LlmToolDefinitionJson.GetRequiredInputSchema(tool).DeepClone();
            var description = LlmToolDefinitionJson.GetDescription(tool);

            return OperationResult<object>.Success(
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters,
                }
            );
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return OperationResult<object>.Failure($"Invalid tool definition: {ex.Message}");
        }
    }

    private static OperationResult<LlmConversationResponse> ParseGeminiResponse(string responseJson, IReadOnlyList<string>? toolNames)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Gemini error envelope: { "error": { "code": 400, "message": "..." } }
            if (root.TryGetProperty("error", out var errEl))
            {
                var errMsg = errEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "Unknown Gemini error." : "Unknown Gemini error.";
                return OperationResult<LlmConversationResponse>.Failure(errMsg);
            }

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                return OperationResult<LlmConversationResponse>.Failure("Gemini response did not contain candidates.");
            }

            var firstCandidate = candidates[0];
            if (!firstCandidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                // Check for safety blocks
                if (firstCandidate.TryGetProperty("finish_reason", out var fr) && string.Equals(fr.GetString(), "SAFETY", StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult<LlmConversationResponse>.Failure("Gemini blocked the response due to safety filters.");
                }

                return OperationResult<LlmConversationResponse>.Failure("Gemini response did not contain content parts.");
            }

            var textParts = new List<string>();
            var toolCalls = new List<LlmToolCall>();

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textParts.Add(text);
                    }
                }
                else if (part.TryGetProperty("function_call", out var funcCallEl))
                {
                    var name = funcCallEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var argsJson = funcCallEl.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}";
                    var callId = Guid.NewGuid().ToString("N");
                    toolCalls.Add(new LlmToolCall(callId, name, argsJson));
                }
            }

            var textContent = textParts.Count > 0 ? string.Join(Environment.NewLine, textParts) : null;

            if (toolCalls.Count == 0)
            {
                var extracted = ExtractToolCallsFromContent(textContent, out var sanitizedContent);
                if (extracted.Count > 0)
                {
                    toolCalls.AddRange(extracted);
                    textContent = sanitizedContent;
                }
            }

            if (toolCalls.Count == 0 && ContainsTextualToolInvocation(textContent, toolNames))
            {
                return OperationResult<LlmConversationResponse>.Failure("Gemini response contained a textual tool invocation instead of a native tool call.");
            }

            var finishReason = firstCandidate.TryGetProperty("finish_reason", out var finishEl) ? finishEl.GetString() ?? string.Empty : string.Empty;

            var tokensUsed = 0;
            if (root.TryGetProperty("usage_metadata", out var usage))
            {
                var totalTokens = usage.TryGetProperty("total_token_count", out var tt) ? tt.GetInt32() : 0;
                tokensUsed = totalTokens;
            }

            return OperationResult<LlmConversationResponse>.Success(new LlmConversationResponse(textContent, tokensUsed, finishReason, toolCalls, null));
        }
        catch (JsonException ex)
        {
            return OperationResult<LlmConversationResponse>.Failure($"Failed to parse Gemini response: {ex.Message}");
        }
    }

    private static string ExtractGeminiErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? responseBody;
            }
        }
        catch { }

        return responseBody.Length > 200 ? responseBody[..200] + "..." : responseBody;
    }

    // -------------------------------------------------------------------------
    // OpenAI-compatible API
    // -------------------------------------------------------------------------

    private OperationResult<Dictionary<string, object?>> BuildConversationPayload(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
    {
        var payload = new Dictionary<string, object?> { ["model"] = model, ["messages"] = messages.Select(BuildMessagePayload).ToList() };

        if (options.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }

        if (options.MaxTokens is int maxTokens)
        {
            payload["max_tokens"] = maxTokens;
        }

        if (options.TopP is double topP)
        {
            payload["top_p"] = topP;
        }

        if (options.FrequencyPenalty is double frequencyPenalty)
        {
            payload["frequency_penalty"] = frequencyPenalty;
        }

        if (options.PresencePenalty is double presencePenalty)
        {
            payload["presence_penalty"] = presencePenalty;
        }

        // LM Studio / Ollama context window override — ignored by OpenAI-hosted endpoints.
        // LM Studio reads "context_length" in the /api/v1/models/load payload (called explicitly
        // by EnsureLmStudioModelLoadedAsync in integration tests); this entry is kept here as a
        // best-effort hint for providers that honour it inside chat-completion requests.
        if (options.ContextLength is int contextLength)
        {
            payload["context_length"] = contextLength;
        }

        if (!string.IsNullOrWhiteSpace(options.ResponseSchema))
        {
            var responseFormatResult = BuildResponseFormat(options.ResponseSchema, options.StrictResponseSchema);
            if (responseFormatResult.IsFailure || responseFormatResult.Value is null)
            {
                return OperationResult<Dictionary<string, object?>>.Failure(responseFormatResult.Error ?? "Invalid response schema.");
            }

            payload["response_format"] = responseFormatResult.Value;
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

                toolPayloads.Add(toolPayloadResult.Value);
            }

            payload["tools"] = toolPayloads;
            payload["parallel_tool_calls"] = options.AllowParallelToolCalls;
            payload["tool_choice"] = MapToolChoice(options.ToolChoiceMode);
        }

        return OperationResult<Dictionary<string, object?>>.Success(payload);
    }

    private static Dictionary<string, object?> BuildMessagePayload(LlmChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = new Dictionary<string, object?> { ["role"] = message.Role };

        if (message.ToolCalls is { Count: > 0 })
        {
            payload["tool_calls"] = message
                .ToolCalls.Select(toolCall => new Dictionary<string, object?>
                {
                    ["id"] = toolCall.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?> { ["name"] = toolCall.Name, ["arguments"] = toolCall.ArgumentsJson },
                })
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            payload["tool_call_id"] = message.ToolCallId;
        }

        if (message.Images is { Count: > 0 })
        {
            var blocks = new List<object>();

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Content });
            }

            foreach (var image in message.Images)
            {
                blocks.Add(new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = $"data:{image.MimeType};base64,{image.Base64Data}" } });
            }

            payload["content"] = blocks;
            return payload;
        }

        payload["content"] = message.Content;
        return payload;
    }

    private static OperationResult<object> BuildToolPayload(JsonObject tool)
    {
        try
        {
            var name = LlmToolDefinitionJson.GetRequiredName(tool);
            var parameters = LlmToolDefinitionJson.GetRequiredInputSchema(tool).DeepClone();
            var description = LlmToolDefinitionJson.GetDescription(tool);

            return OperationResult<object>.Success(
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["description"] = description,
                        ["parameters"] = parameters,
                    },
                }
            );
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return OperationResult<object>.Failure($"Invalid tool definition: {ex.Message}");
        }
    }

    private static OperationResult<object> BuildResponseFormat(string responseSchema, bool strict)
    {
        try
        {
            var schema = JsonNode.Parse(responseSchema) ?? throw new JsonException("Parsed schema was null.");

            return OperationResult<object>.Success(
                new Dictionary<string, object?>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object?>
                    {
                        ["name"] = "response",
                        ["strict"] = (object)strict,
                        ["schema"] = schema,
                    },
                }
            );
        }
        catch (JsonException ex)
        {
            return OperationResult<object>.Failure($"Invalid response schema: {ex.Message}");
        }
    }

    private async Task<OperationResult<LlmConversationResponse>> SendConversationRequestAsync(Dictionary<string, object?> payload, IReadOnlyList<string>? toolNames, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = LlmRequestTimeout;
        var endpoint = $"{_baseUrl}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var requestJson = JsonSerializer.Serialize(payload, SerializerOptions);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        _logger.LogDebug("[LLM] --> {Endpoint} messages={MessageCount} chars={Chars}", endpoint, payload.TryGetValue("messages", out var msgs) && msgs is System.Collections.ICollection c ? c.Count : 0, requestJson.Length);
        _logger.LogDebug("[LLM] --> payload{NewLine}{Payload}", Environment.NewLine, requestJson);

        try
        {
            using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogError("LLM API error: {StatusCode} {Body}", httpResponse.StatusCode, responseBody);
                return OperationResult<LlmConversationResponse>.Failure(LlmProviderError.Describe("LLM", httpResponse.StatusCode, ExtractErrorMessage(responseBody)));
            }

            TryExtractUsage(responseBody, out var promptTokens, out var completionTokens, out var totalTokens);
            _logger.LogDebug("[LLM] <-- {StatusCode} prompt_tokens={PromptTokens} completion_tokens={CompletionTokens} total_tokens={TotalTokens}", (int)httpResponse.StatusCode, promptTokens, completionTokens, totalTokens);
            _logger.LogDebug("[LLM] <-- response{NewLine}{Body}", Environment.NewLine, responseBody);

            return ParseConversationResponse(responseBody, toolNames);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "LLM API request failed");
            return OperationResult<LlmConversationResponse>.Failure($"LLM request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "LLM API request was canceled by the caller");
            return OperationResult<LlmConversationResponse>.Failure("LLM request was canceled before completion.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "LLM API request timed out after {Timeout}", LlmRequestTimeout);
            return OperationResult<LlmConversationResponse>.Failure($"LLM request timed out after {LlmRequestTimeout.TotalSeconds}s. The model may need more time for complex prompts.");
        }
    }

    private static void TryExtractUsage(string responseJson, out int promptTokens, out int completionTokens, out int totalTokens)
    {
        promptTokens = 0;
        completionTokens = 0;
        totalTokens = 0;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            // OpenAI: { usage: { prompt_tokens, completion_tokens, total_tokens } }
            // Anthropic: { usage: { input_tokens, output_tokens } }
            if (!root.TryGetProperty("usage", out var usage))
                return;
            if (usage.TryGetProperty("prompt_tokens", out var pt))
                promptTokens = pt.GetInt32();
            else if (usage.TryGetProperty("input_tokens", out var it))
                promptTokens = it.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct))
                completionTokens = ct.GetInt32();
            else if (usage.TryGetProperty("output_tokens", out var ot))
                completionTokens = ot.GetInt32();
            if (usage.TryGetProperty("total_tokens", out var tt))
                totalTokens = tt.GetInt32();
            else
                totalTokens = promptTokens + completionTokens;
        }
        catch (JsonException) { }
    }

    private static OperationResult<LlmConversationResponse> ParseConversationResponse(string responseJson, IReadOnlyList<string>? toolNames)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return OperationResult<LlmConversationResponse>.Failure("LLM response did not contain choices.");
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message))
            {
                return OperationResult<LlmConversationResponse>.Failure("LLM response did not contain a message payload.");
            }

            var content = ExtractMessageContent(message);
            var reasoningContent = ExtractReasoningContent(message);
            var toolCalls = ExtractToolCalls(message);
            if (toolCalls.Count == 0)
            {
                toolCalls = ExtractToolCallsFromContent(content, out var sanitizedContent);
                if (toolCalls.Count > 0)
                {
                    content = sanitizedContent;
                }
            }

            if (string.IsNullOrWhiteSpace(content) && toolCalls.Count == 0 && message.TryGetProperty("refusal", out var refusalElement) && refusalElement.ValueKind == JsonValueKind.String)
            {
                var refusal = refusalElement.GetString();
                if (!string.IsNullOrWhiteSpace(refusal))
                {
                    return OperationResult<LlmConversationResponse>.Failure($"LLM refused to produce structured output: {refusal}");
                }
            }

            if (toolCalls.Count == 0 && ContainsTextualToolInvocation(content, toolNames))
            {
                return OperationResult<LlmConversationResponse>.Failure("LLM response contained a textual tool invocation instead of a native tool call.");
            }

            var finishReason = firstChoice.TryGetProperty("finish_reason", out var finishReasonElement) ? finishReasonElement.GetString() ?? string.Empty : string.Empty;

            var tokensUsed = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var totalTokens))
            {
                tokensUsed = totalTokens.GetInt32();
            }

            return OperationResult<LlmConversationResponse>.Success(new LlmConversationResponse(content, tokensUsed, finishReason, toolCalls, reasoningContent));
        }
        catch (JsonException ex)
        {
            return OperationResult<LlmConversationResponse>.Failure($"Failed to parse LLM response: {ex.Message}");
        }
    }

    private static string? ExtractReasoningContent(JsonElement message)
    {
        if (!message.TryGetProperty("reasoning_content", out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }

    private static string? ExtractMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var contentElement))
        {
            return null;
        }

        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString(),
            JsonValueKind.Array => string.Join(Environment.NewLine, contentElement.EnumerateArray().Select(ExtractContentPart).Where(text => !string.IsNullOrWhiteSpace(text))),
            JsonValueKind.Null => null,
            _ => contentElement.GetRawText(),
        };
    }

    private static string? ExtractContentPart(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            return part.GetString();
        }

        if (part.ValueKind != JsonValueKind.Object)
        {
            return part.GetRawText();
        }

        if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString();
        }

        if (part.TryGetProperty("type", out var typeElement) && string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) && part.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString();
        }

        return part.GetRawText();
    }

    private static IReadOnlyList<LlmToolCall> ExtractToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement) || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var toolCalls = new List<LlmToolCall>();

        foreach (var toolCallElement in toolCallsElement.EnumerateArray())
        {
            if (!toolCallElement.TryGetProperty("id", out var idElement) || !toolCallElement.TryGetProperty("function", out var functionElement) || !functionElement.TryGetProperty("name", out var nameElement) || !functionElement.TryGetProperty("arguments", out var argumentsElement))
            {
                continue;
            }

            toolCalls.Add(new LlmToolCall(idElement.GetString() ?? Guid.NewGuid().ToString("N"), nameElement.GetString() ?? string.Empty, argumentsElement.ValueKind == JsonValueKind.String ? argumentsElement.GetString() ?? "{}" : argumentsElement.GetRawText()));
        }

        return toolCalls;
    }

    private static bool ContainsTextualToolInvocation(string? content, IReadOnlyList<string>? toolNames)
    {
        if (string.IsNullOrWhiteSpace(content) || toolNames is not { Count: > 0 })
        {
            return false;
        }

        if (!TextualToolInvocationRegex.IsMatch(content))
        {
            return false;
        }

        return toolNames.Any(toolName => !string.IsNullOrWhiteSpace(toolName) && content.Contains($"{toolName}(", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<LlmToolCall> ExtractToolCallsFromContent(string? content, out string? sanitizedContent)
    {
        sanitizedContent = content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var matches = ToolCallMarkupRegex.Matches(content);
        if (matches.Count == 0)
        {
            return [];
        }

        var toolCalls = new List<LlmToolCall>(matches.Count);
        foreach (Match match in matches)
        {
            if (!TryParseToolCallMarkup(match.Groups[1].Value, out var toolCall))
            {
                sanitizedContent = content;
                return [];
            }

            toolCalls.Add(toolCall!);
        }

        var remainingContent = ToolCallMarkupRegex.Replace(content, string.Empty).Trim();
        sanitizedContent = string.IsNullOrWhiteSpace(remainingContent) ? null : remainingContent;
        return toolCalls;
    }

    private static bool TryParseToolCallMarkup(string json, out LlmToolCall? toolCall)
    {
        toolCall = null;

        try
        {
            using var document = JsonDocument.Parse(json.Trim());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String || !root.TryGetProperty("arguments", out var argumentsElement))
            {
                return false;
            }

            toolCall = new LlmToolCall(
                root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                nameElement.GetString() ?? string.Empty,
                argumentsElement.ValueKind == JsonValueKind.String ? argumentsElement.GetString() ?? "{}" : argumentsElement.GetRawText()
            );

            return !string.IsNullOrWhiteSpace(toolCall.Name);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object MapToolChoice(LlmToolChoiceMode mode) =>
        mode switch
        {
            LlmToolChoiceMode.None => "none",
            LlmToolChoiceMode.Required => "required",
            _ => "auto",
        };

    private static string ExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? responseBody;
            }
        }
        catch { }

        return responseBody.Length > 200 ? responseBody[..200] + "..." : responseBody;
    }
}

/// <summary>
/// Response from an LLM completion request.
/// </summary>
public sealed record LlmResponse(string Content, int TokensUsed);
