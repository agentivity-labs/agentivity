using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgentConversationBuilder
{
    private const string AgentWorkspaceSystemPromptPrefix = "Structured agent workspace from previous turns:";
    private const string AgentWorkspaceClosingSentence = "Use this agent workspace to preserve explicit facts, constraints, decisions, and open questions unless newer conversation contradicts them.";
    private const string ToolFailureRecoveryPrompt = "One or more tool attempts have failed. Search for an alternative, if possible. Do not repeat the same failed tool call with the same parameters unless the user provides materially new information.";

    public static List<LlmChatMessage> BuildInitialMessages(
        string systemPrompt,
        string prompt,
        string executionPolicyPrompt,
        string agentWorkspacePrompt,
        string humanLoopSystemPrompt,
        string sessionSummary,
        IReadOnlyList<LlmChatMessage> restoredMessages,
        bool appendPrompt = true,
        string? teamContextPrompt = null,
        IReadOnlyList<ImageAttachment>? images = null
    )
    {
        var messages = new List<LlmChatMessage>();

        // Some chat templates (Jinja-based, e.g. Qwen's) reject any "system" message that isn't
        // the very first message in the list — not just ones inserted out of order, but a second,
        // third, etc. consecutive one too. So every system-level prompt fragment is merged into a
        // single "system" message instead of being appended as separate entries.
        var systemSections = new List<string>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            systemSections.Add(systemPrompt);
        }

        systemSections.Add(AgentPrompting.SharedExecutionSystemPrompt);

        if (!string.IsNullOrWhiteSpace(teamContextPrompt))
        {
            systemSections.Add(teamContextPrompt);
        }

        if (!string.IsNullOrWhiteSpace(executionPolicyPrompt))
        {
            systemSections.Add(executionPolicyPrompt);
        }

        if (!string.IsNullOrWhiteSpace(agentWorkspacePrompt))
        {
            systemSections.Add(BuildAgentWorkspaceContent(agentWorkspacePrompt));
        }

        if (!string.IsNullOrWhiteSpace(humanLoopSystemPrompt))
        {
            systemSections.Add(humanLoopSystemPrompt);
        }

        if (!string.IsNullOrWhiteSpace(sessionSummary))
        {
            systemSections.Add($"Summary of the conversation so far (before the messages shown below):\n{sessionSummary}\n\nTreat this as compressed history from earlier turns — your own and, for a team member, any other team member's.");
        }

        messages.Add(new LlmChatMessage("system", string.Join("\n\n", systemSections)));

        if (restoredMessages.Count > 0)
        {
            messages.AddRange(restoredMessages.Where(ShouldRestoreMessage).Select(CloneMessage));
        }

        if (appendPrompt)
        {
            messages.Add(new LlmChatMessage("user", prompt, Images: images is { Count: > 0 } ? images : null));
        }

        return messages;
    }

    // The agent workspace section lives inside the single leading "system" message (see
    // BuildInitialMessages) rather than as its own message — inserting a second "system" message
    // here, even at a computed index, would re-trigger the same single-system-message template
    // constraint that BuildInitialMessages was written to avoid.
    public static void ReplaceAgentWorkspacePrompt(List<LlmChatMessage> messages, string? agentWorkspacePrompt)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0 || !string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var content = RemoveAgentWorkspaceSection(messages[0].Content ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(agentWorkspacePrompt))
        {
            content = string.IsNullOrEmpty(content)
                ? BuildAgentWorkspaceContent(agentWorkspacePrompt)
                : $"{content}\n\n{BuildAgentWorkspaceContent(agentWorkspacePrompt)}";
        }

        messages[0] = messages[0] with { Content = content };
    }

    private static string RemoveAgentWorkspaceSection(string content)
    {
        var startIndex = content.IndexOf(AgentWorkspaceSystemPromptPrefix, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return content;
        }

        var closingIndex = content.IndexOf(AgentWorkspaceClosingSentence, startIndex, StringComparison.Ordinal);
        if (closingIndex < 0)
        {
            return content;
        }

        var removalStart = startIndex;
        if (removalStart >= 2 && content[removalStart - 1] == '\n' && content[removalStart - 2] == '\n')
        {
            removalStart -= 2;
        }

        var removalEnd = closingIndex + AgentWorkspaceClosingSentence.Length;
        return content.Remove(removalStart, removalEnd - removalStart);
    }

    private static string BuildAgentWorkspaceContent(string agentWorkspacePrompt) =>
        $"{AgentWorkspaceSystemPromptPrefix}\n{agentWorkspacePrompt}\n\n{AgentWorkspaceClosingSentence}";

    private static bool ShouldRestoreMessage(LlmChatMessage message)
    {
        // System messages are never replayed from a saved session: this method's caller already
        // rebuilds fresh ones (systemPrompt, executionPolicyPrompt, etc.) at the very top of the new
        // message list on every turn. Replaying an old saved one here would insert a second "system"
        // message in the middle of the conversation — some chat templates (Jinja-based, e.g. Qwen's)
        // hard-require every "system" message to be at the very beginning and reject the request
        // outright if one shows up later.
        if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Assistant messages that carry only tool calls (no text content) are intentionally
        // preserved: filtering them out produces orphaned tool results that confuse the LLM
        // (e.g. HIL resumes where the model re-asks the same question because it never sees
        // the original ask_human invocation in the restored history).

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return true;
        }

        var content = message.Content.Trim();
        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return !content.StartsWith(AgentPrompting.ResponseQualityRefinementPromptPrefix, StringComparison.Ordinal)
                && !content.StartsWith(ToolFailureRecoveryPrompt, StringComparison.Ordinal)
                && !string.Equals(content, AgentPrompting.StructuredResponseFinalizationPrompt, StringComparison.Ordinal)
                && !content.StartsWith(AgentPrompting.DirectAnswerFinalizationPromptPrefix, StringComparison.Ordinal)
                && !content.StartsWith("<final_response_request>", StringComparison.Ordinal)
                && !content.StartsWith("Execute the user request by following this plan", StringComparison.Ordinal)
                && !content.StartsWith("Execute the user request by following this revised plan", StringComparison.Ordinal)
                && !content.StartsWith("Revise the execution plan based on the current conversation", StringComparison.Ordinal);
        }

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) && LooksLikePlanPayload(content))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikePlanPayload(string content)
    {
        if (!(content.StartsWith('{') && content.EndsWith('}')))
        {
            return false;
        }

        return content.Contains("\"goal\"", StringComparison.Ordinal) && content.Contains("\"steps\"", StringComparison.Ordinal);
    }

    private static LlmChatMessage CloneMessage(LlmChatMessage message) => LlmChatMessageCloner.CloneMessage(message);
}
