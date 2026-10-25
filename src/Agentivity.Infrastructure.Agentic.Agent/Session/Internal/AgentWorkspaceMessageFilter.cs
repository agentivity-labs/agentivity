using System.Text.Json;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentWorkspaceMessageFilter
{
    internal static bool ShouldIgnoreForAgentWorkspace(LlmChatMessage message, AgentWorkspacePolicyOptions options)
    {
        if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(message.Content))
        {
            return true;
        }

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            if (LooksLikeJsonPayload(message.Content) || IsInternalOrchestrationText(message.Content))
            {
                return true;
            }

            if (ContainsNaturalQuestion(message.Content))
            {
                return true;
            }

            return options.DisallowAssistantSuggestionPromotion ? !ContainsNaturalQuestion(message.Content) : false;
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) && (IsHumanLoopToolTranscript(message.Content) || IsHumanLoopToolPayload(message.Content) || IsBlockedHumanLoopToolResult(message.Content)))
        {
            return true;
        }

        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) && IsInternalOrchestrationText(message.Content))
        {
            return true;
        }

        return false;
    }

    internal static bool ShouldIgnoreStandaloneUserFact(string value)
    {
        var normalizedValue = AgentWorkspaceTextNormalizer.NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return true;
        }

        if (normalizedValue.Any(char.IsDigit) || normalizedValue.Contains(' '))
        {
            return false;
        }

        if (AgentWorkspaceHeuristicClassifier.LooksLikeGoal(normalizedValue) || AgentWorkspaceHeuristicClassifier.LooksLikeConstraint(normalizedValue) || AgentWorkspaceHeuristicClassifier.LooksLikeDecisionOrPreference(normalizedValue))
        {
            return false;
        }

        return normalizedValue.Length <= 24;
    }

    internal static bool IsHumanLoopToolTranscript(string content)
    {
        var normalizedContent = AgentWorkspaceTextNormalizer.NormalizeText(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return false;
        }

        return normalizedContent.Contains("Question asked:", StringComparison.OrdinalIgnoreCase) || normalizedContent.Contains("Questions asked:", StringComparison.OrdinalIgnoreCase) || normalizedContent.Contains("Human response:", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsHumanLoopToolPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || !LooksLikeJsonPayload(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            return root.TryGetProperty("duplicateBlocked", out _) || root.TryGetProperty("question", out _) || root.TryGetProperty("questionsToAsk", out _) || root.TryGetProperty("responseText", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsBlockedHumanLoopToolResult(string content)
    {
        var normalizedContent = AgentWorkspaceTextNormalizer.NormalizeText(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return false;
        }

        return normalizedContent.StartsWith("duplicateBlocked: true", StringComparison.OrdinalIgnoreCase) || normalizedContent.StartsWith("duplicate blocked: true", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsNaturalQuestion(string content) => content.Contains('?', StringComparison.Ordinal) && !LooksLikeJsonPayload(content);

    internal static bool LooksLikeJsonPayload(string content)
    {
        var trimmed = content.Trim();
        return (trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']'));
    }

    internal static bool IsInternalOrchestrationText(string content) =>
        content.Contains("Execution plan:", StringComparison.Ordinal)
        || content.Contains("Original user request:", StringComparison.Ordinal)
        || content.Contains("Execute the user request by following this plan", StringComparison.Ordinal)
        || content.Contains("Execute the user request by following this revised plan", StringComparison.Ordinal)
        || content.Contains("Failed tool attempts to avoid repeating:", StringComparison.Ordinal)
        || content.Contains("Answer the original user request directly now", StringComparison.Ordinal)
        || content.Contains("Quality refinement feedback for the previous answer:", StringComparison.Ordinal)
        || content.Contains("A human has already answered the previous clarification request", StringComparison.Ordinal);
}
