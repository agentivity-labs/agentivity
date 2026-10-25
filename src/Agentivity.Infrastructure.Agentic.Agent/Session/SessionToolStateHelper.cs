using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class SessionToolStateHelper
{
    public static Dictionary<string, JsonObject> CloneToolState(IReadOnlyDictionary<string, JsonObject> toolState) => toolState.ToDictionary(kvp => kvp.Key, kvp => (JsonObject)kvp.Value.DeepClone(), StringComparer.OrdinalIgnoreCase);

    public static void UpdateSessionToolState(SessionContext sessionContext, LlmToolCall toolCall, string resultContent, bool isError)
    {
        if (!sessionContext.Enabled)
        {
            return;
        }

        sessionContext.State.ToolState[toolCall.Name] = new JsonObject
        {
            ["toolCallId"] = toolCall.Id,
            ["toolName"] = toolCall.Name,
            ["argumentsJson"] = toolCall.ArgumentsJson,
            ["resultContent"] = resultContent,
            ["isError"] = isError,
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public static bool TryReuseSessionToolState(SessionContext sessionContext, IReadOnlySet<string> reusableToolNames, LlmToolCall toolCall, out string resultContent)
    {
        resultContent = string.Empty;

        if (!sessionContext.Enabled || sessionContext.Binding is null)
        {
            return false;
        }

        var reuseMode = sessionContext.Binding.GetString("tool_state_reuse_mode")?.Trim().ToLowerInvariant() ?? "none";
        if (!string.Equals(reuseMode, "exact_match", StringComparison.Ordinal) || !reusableToolNames.Contains(toolCall.Name))
        {
            return false;
        }

        if (!sessionContext.State.ToolState.TryGetValue(toolCall.Name, out var cachedState))
        {
            return false;
        }

        var cachedArgumentsJson = cachedState["argumentsJson"]?.GetValue<string>();
        if (!string.Equals(cachedArgumentsJson, toolCall.ArgumentsJson, StringComparison.Ordinal))
        {
            return false;
        }

        if (cachedState["isError"]?.GetValue<bool>() == true)
        {
            return false;
        }

        var maxAgeSeconds = Math.Max(0, sessionContext.Binding.GetInt("tool_state_reuse_ttl_seconds") ?? 3600);
        if (maxAgeSeconds > 0 && DateTimeOffset.TryParse(cachedState["updatedAtUtc"]?.GetValue<string>(), out var updatedAtUtc) && updatedAtUtc < DateTimeOffset.UtcNow.AddSeconds(-maxAgeSeconds))
        {
            return false;
        }

        resultContent = cachedState["resultContent"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resultContent))
        {
            return false;
        }

        cachedState["reuseCount"] = (cachedState["reuseCount"]?.GetValue<int>() ?? 0) + 1;
        cachedState["lastReusedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        return true;
    }
}
