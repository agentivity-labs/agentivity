using System.Text.Json;

namespace Agentivity.Infrastructure.Core.Json;

/// <summary>
/// Shared parsing helper for "content blocks" arguments (e.g. <c>interact_with_human</c>'s
/// <c>content</c> array) — used by both the agent-side HIL pending-review resolution
/// (<c>HumanLoopHelper</c>) and the chat channel's rendering path (<c>ChatChannel</c>), which
/// otherwise each need to tolerate the exact same LLM quirk independently.
/// </summary>
public static class JsonContentBlocks
{
    /// <summary>
    /// Some small local models emit array-typed tool arguments as a JSON-encoded string instead
    /// of a native array (e.g. <c>"content": "[{...}]"</c> instead of <c>"content": [{...}]</c>).
    /// Accepts either shape and returns the resolved array element, or <see langword="null"/> if
    /// neither applies.
    /// </summary>
    public static JsonElement? NormalizeContentArrayElement(JsonElement contentElement)
    {
        if (contentElement.ValueKind == JsonValueKind.Array)
            return contentElement;

        if (contentElement.ValueKind == JsonValueKind.String && contentElement.GetString() is { Length: > 0 } inner)
        {
            try
            {
                using var innerDocument = JsonDocument.Parse(inner);
                if (innerDocument.RootElement.ValueKind == JsonValueKind.Array)
                    return innerDocument.RootElement.Clone();
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        return null;
    }
}
