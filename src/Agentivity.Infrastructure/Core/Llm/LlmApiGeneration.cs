using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// One call, as a generation is given it: how to reach the provider, where (<paramref name="Address"/>, also what a model is
/// known by), with which key, which model, and what is asked.
/// </summary>
internal sealed record LlmApiCall(ILlmTransport Transport, string Address, string ApiKey, string Model, IReadOnlyList<LlmChatMessage> Messages, IReadOnlyList<JsonObject>? Tools, LlmConversationOptions Options, ILogger Logger)
{
    /// <summary>The names of the tools offered, to tell a real tool call from one written as text.</summary>
    public IReadOnlyList<string>? ToolNames { get; } = Tools is { Count: > 0 } ? Tools.Select(LlmToolDefinitionJson.GetRequiredName).ToList() : null;
}

/// <summary>
/// One generation of a provider's API: the models it covers, and how to speak to them — how a request is written, where it
/// goes, what it may carry, how the answer reads. A provider's API changes over time, sometimes deeply; each generation is a
/// class of its own, and a new one is added beside the others without touching them. A provider's client
/// (<see cref="LlmClient"/>) holds its generations and speaks to a model with the one that covers it. A generation knows
/// nothing of the transport: the same one is spoken over HTTP or through AWS (Claude on Bedrock).
/// </summary>
public abstract class LlmApiGeneration
{
    private static readonly Regex ToolCallMarkupRegex = new(@"<tool_call>\s*(.*?)\s*</tool_call>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex TextualToolInvocationRegex = new(@"```[\s\S]*?[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Whether this generation is the way to speak to <paramref name="model"/>.</summary>
    public abstract bool Covers(string model);

    /// <summary>Sends the call and reads the answer, adapting the request as this generation knows how.</summary>
    internal abstract Task<OperationResult<LlmConversationResponse>> SendAsync(LlmApiCall call, CancellationToken cancellationToken);

    // ---- What every generation reads the same way ------------------------------------------------------------------------

    private protected static object? TryParseJsonObject(string? json)
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

    /// <summary>A tool call written as a fenced call in the text (<c>tool_name(…)</c>) instead of a native one.</summary>
    private protected static bool ContainsTextualToolInvocation(string? content, IReadOnlyList<string>? toolNames)
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

    /// <summary>Tool calls a model wrote as <c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c> markup, and the text without them.</summary>
    private protected static IReadOnlyList<LlmToolCall> ExtractToolCallsFromContent(string? content, out string? sanitizedContent)
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
}
