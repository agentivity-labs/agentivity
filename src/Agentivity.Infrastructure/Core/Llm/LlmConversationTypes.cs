using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Conversation-oriented options for chat-based LLM requests.
/// Used by agentic runtimes that need message history and tool calling.
/// </summary>
public sealed record LlmConversationOptions(
    double? Temperature = null,
    int? MaxTokens = null,
    string? ResponseSchema = null,
    double? TopP = null,
    /// <summary>
    /// Defaults to a mild anti-repetition nudge (OpenAI-compatible range, typically -2..2) —
    /// without it, small local models at low temperature can lock into repeating themselves
    /// verbatim until they exhaust MaxTokens instead of reaching a clean stop (observed with
    /// Qwen2.5-14B via LM Studio: a turn that should have ended in a few sentences instead
    /// produced ~40 near-identical paragraphs and never emitted finish_reason "stop").
    /// </summary>
    double? FrequencyPenalty = 0.3,
    double? PresencePenalty = null,
    LlmToolChoiceMode ToolChoiceMode = LlmToolChoiceMode.Auto,
    bool AllowParallelToolCalls = false,
    bool StrictResponseSchema = false,
    /// <summary>
    /// Context window size override (tokens). Passed as <c>context_length</c> in the LM Studio / Ollama
    /// API request body. Ignored by OpenAI-hosted endpoints. Use this in tests to raise the
    /// context limit beyond the server default (typically 4096) when prompts are large.
    /// </summary>
    int? ContextLength = null
);

/// <summary>
/// A single chat message in a conversation sent to the LLM.
/// </summary>
/// <param name="IsPlaceholder">
/// True for a message that stands in for content that doesn't exist yet — e.g. the HIL
/// suspension marker (<c>HilWaitProxy.WaitForHumanReviewAsync</c>), which carries the same
/// <see cref="ToolCallId"/> as the tool_use it's temporarily covering for so the conversation
/// stays API-valid while suspended. Structurally it looks like a real answer to anything that
/// only matches ids — it isn't one. Consumers that decide what's safe to summarize/discard
/// (<c>SummaryStep</c>) must never treat a placeholder as a genuine resolution.
/// </param>
public sealed record LlmChatMessage(string Role, string? Content, string? ToolCallId = null, IReadOnlyList<LlmToolCall>? ToolCalls = null, bool IsPlaceholder = false, IReadOnlyList<ImageAttachment>? Images = null)
{
    public override string ToString()
    {
        if (ToolCalls is { Count: > 0 })
            return $"[{Role}] <{ToolCalls.Count} tool call(s): {string.Join(", ", ToolCalls.Select(t => t.Name))}>";
        if (ToolCallId is not null)
            return $"[{Role}] tool_result({ToolCallId}): {Content?.Length ?? 0} chars";
        //var preview = Content is { Length: > 80 } ? Content[..80] + "..." : Content ?? string.Empty;
        var preview = Content;
        return $"[{Role}] {preview}";
    }
}

/// <summary>
/// Helper methods for the canonical JSON representation of a tool definition.
/// The JSON shape is MCP-like: <c>{ name, description, inputSchema }</c>.
/// Provider adapters are responsible for translating this payload to model-specific formats.
/// </summary>
public static class LlmToolDefinitionJson
{
    public static JsonObject Create(string name, string description, string inputSchema, bool allowToolStateReuse = false, bool requiresHumanReview = false, bool isRoutingTool = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (string.IsNullOrWhiteSpace(inputSchema))
        {
            throw new ArgumentException("Tool input schema is required.", nameof(inputSchema));
        }

        JsonNode schemaNode;
        try
        {
            schemaNode = JsonNode.Parse(inputSchema) ?? throw new JsonException("Parsed tool schema was null.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Tool '{name}' has an invalid JSON schema: {ex.Message}", nameof(inputSchema), ex);
        }

        var tool = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schemaNode,
        };

        if (allowToolStateReuse || requiresHumanReview || isRoutingTool)
        {
            var executionHints = new JsonObject();

            if (allowToolStateReuse)
            {
                executionHints["allowToolStateReuse"] = true;
            }

            if (requiresHumanReview)
            {
                executionHints["requiresHumanReview"] = true;
            }

            if (isRoutingTool)
            {
                executionHints["isRoutingTool"] = true;
            }

            tool["executionHints"] = executionHints;
        }

        return tool;
    }

    public static string GetRequiredName(JsonObject tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (!tool.TryGetPropertyValue("name", out var nameNode) || nameNode is null || nameNode.GetValueKind() != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool definition is missing a string 'name' property.");
        }

        var name = nameNode.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Tool definition contains an empty 'name' property.");
        }

        return name;
    }

    public static string? GetDescription(JsonObject tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (!tool.TryGetPropertyValue("description", out var descriptionNode) || descriptionNode is null)
        {
            return null;
        }

        return descriptionNode.GetValueKind() == JsonValueKind.String ? descriptionNode.GetValue<string>() : descriptionNode.ToJsonString();
    }

    public static JsonNode GetRequiredInputSchema(JsonObject tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (!tool.TryGetPropertyValue("inputSchema", out var inputSchemaNode) || inputSchemaNode is null)
        {
            throw new InvalidOperationException("Tool definition is missing an 'inputSchema' property.");
        }

        return inputSchemaNode;
    }

    public static bool SupportsToolStateReuse(JsonObject tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return tool.TryGetPropertyValue("executionHints", out var hintsNode) && hintsNode is JsonObject hints && hints["allowToolStateReuse"]?.GetValueKind() is JsonValueKind.True;
    }

    public static bool RequiresHumanReview(JsonObject tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return tool.TryGetPropertyValue("executionHints", out var hintsNode) && hintsNode is JsonObject hints && hints["requiresHumanReview"]?.GetValueKind() is JsonValueKind.True;
    }
}

/// <summary>
/// A tool call requested by the LLM.
/// </summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson)
{
    public override string ToString() => $"{Name}({ArgumentsJson})";
}

/// <summary>
/// Response returned by a conversation-oriented LLM request.
/// </summary>
public sealed record LlmConversationResponse(string? Content, int TokensUsed, string FinishReason, IReadOnlyList<LlmToolCall> ToolCalls, string? ReasoningContent = null, bool NeedsHumanInput = false, bool IsSelectionQuestion = false)
{
    public override string ToString()
    {
        if (ToolCalls is { Count: > 0 })
            return $"[{FinishReason}] {ToolCalls.Count} tool call(s): {string.Join(", ", ToolCalls)} ({TokensUsed} tokens)";
        var preview = Content is { Length: > 80 } ? Content[..80] + "..." : Content ?? string.Empty;
        return $"[{FinishReason}] {preview} ({TokensUsed} tokens)";
    }
}

/// <summary>
/// Tool choice strategy for the LLM request.
/// </summary>
public enum LlmToolChoiceMode
{
    Auto,
    None,
    Required,
}
