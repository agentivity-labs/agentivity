using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// OpenAI's Chat Completions API (<c>/chat/completions</c>, the key as a bearer token) — the format most providers share:
/// DeepSeek, xAI, Groq, Ollama, LM Studio, Azure OpenAI.
/// </summary>
public sealed class OpenAiChatCompletions : LlmApiGeneration
{
    /// <summary>Every model spoken to in OpenAI's format, so far.</summary>
    public override bool Covers(string model) => true;

    internal override async Task<OperationResult<LlmConversationResponse>> SendAsync(LlmApiCall call, CancellationToken cancellationToken)
    {
        var payloadResult = BuildPayload(call.Model, call.Messages, call.Tools, call.Options);
        if (payloadResult.IsFailure || payloadResult.Value is null)
        {
            return OperationResult<LlmConversationResponse>.Failure(payloadResult.Error ?? "Failed to build LLM payload.");
        }

        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {call.ApiKey}" };
        var reply = await call.Transport.SendAsync(new LlmWireRequest("LLM", "/chat/completions", payloadResult.Value, headers, call.Model), cancellationToken).ConfigureAwait(false);
        if (reply.Body.IsFailure || reply.Body.Value is null)
        {
            return OperationResult<LlmConversationResponse>.Failure(reply.Body.Error ?? "LLM request failed.");
        }

        return ParseResponse(reply.Body.Value, call.ToolNames);
    }

    internal static OperationResult<Dictionary<string, object?>> BuildPayload(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
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

    private static OperationResult<LlmConversationResponse> ParseResponse(string responseJson, IReadOnlyList<string>? toolNames)
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

    private static object MapToolChoice(LlmToolChoiceMode mode) =>
        mode switch
        {
            LlmToolChoiceMode.None => "none",
            LlmToolChoiceMode.Required => "required",
            _ => "auto",
        };
}
