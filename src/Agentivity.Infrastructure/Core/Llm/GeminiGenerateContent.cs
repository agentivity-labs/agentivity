using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Google's Gemini API through AI Studio (<c>/models/{model}:generateContent</c>, the key in the query): the system
/// instruction beside the contents, function declarations for the tools.
/// </summary>
public sealed class GeminiGenerateContent : LlmApiGeneration
{
    /// <summary>Every Gemini model, so far.</summary>
    public override bool Covers(string model) => true;

    internal override async Task<OperationResult<LlmConversationResponse>> SendAsync(LlmApiCall call, CancellationToken cancellationToken)
    {
        var payloadResult = BuildPayload(call.Messages, call.Tools, call.Options);
        if (payloadResult.IsFailure || payloadResult.Value is null)
        {
            return OperationResult<LlmConversationResponse>.Failure(payloadResult.Error ?? "Failed to build Gemini payload.");
        }

        var path = $"/models/{call.Model}:generateContent?key={Uri.EscapeDataString(call.ApiKey)}";
        var reply = await call.Transport.SendAsync(new LlmWireRequest("Gemini", path, payloadResult.Value, new Dictionary<string, string>(), call.Model), cancellationToken).ConfigureAwait(false);
        if (reply.Body.IsFailure || reply.Body.Value is null)
        {
            return OperationResult<LlmConversationResponse>.Failure(reply.Body.Error ?? "Gemini request failed.");
        }

        return ParseResponse(reply.Body.Value, call.ToolNames);
    }

    private static OperationResult<Dictionary<string, object?>> BuildPayload(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
    {
        // Gemini separates system instruction from the conversation contents.
        var systemMessage = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        var conversationMessages = messages.Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).Select(BuildContentPayload).ToList();

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

    private static Dictionary<string, object?> BuildContentPayload(LlmChatMessage message)
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

    private static OperationResult<LlmConversationResponse> ParseResponse(string responseJson, IReadOnlyList<string>? toolNames)
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
}
