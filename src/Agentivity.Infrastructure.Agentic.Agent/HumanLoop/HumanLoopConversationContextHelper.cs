using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class HumanLoopConversationContextHelper
{
    public const string HumanLoopClarificationsMetadataKey = "human_loop_clarifications";

    private static readonly ConcurrentDictionary<Guid, List<HumanLoopClarificationState>> ClarificationsByRun = new();

    public static string BuildConnectedHumanLoopSystemPrompt(bool allowAskHuman, Agentivity.Infrastructure.Workflows.Interactions.AgentHumanLoopCapabilities humanLoopCapabilities, IReadOnlyList<HumanLoopClarificationState> clarifications)
    {
        if (!allowAskHuman)
        {
            return string.Empty;
        }

        if (!humanLoopCapabilities.SupportsAskHuman)
        {
            throw new InvalidOperationException("The connected human-in-the-loop channel does not expose any agent-driven human-loop tools.");
        }

        var instructions = humanLoopCapabilities.InternalInstructions.Where(instruction => !string.IsNullOrWhiteSpace(instruction)).Select(instruction => instruction.Trim()).ToList();

        return string.Join(Environment.NewLine + Environment.NewLine, instructions);
    }

    public static IReadOnlyList<HumanLoopClarificationState> GetClarificationLedger(Guid runId)
    {
        if (!ClarificationsByRun.TryGetValue(runId, out var clarifications))
        {
            return [];
        }

        lock (clarifications)
        {
            return clarifications.ToList();
        }
    }

    public static void ClearClarificationLedger(Guid runId) => ClarificationsByRun.TryRemove(runId, out _);

    public static void RestoreClarificationLedger(Guid runId, IReadOnlyDictionary<string, object?> metadata)
    {
        var restoredClarifications = HumanLoopLedgerSerializer.RestoreClarificationLedger(metadata, HumanLoopClarificationsMetadataKey);

        if (restoredClarifications is null || restoredClarifications.Count == 0)
        {
            return;
        }

        var clarifications = ClarificationsByRun.GetOrAdd(runId, static _ => []);
        lock (clarifications)
        {
            clarifications.Clear();
            clarifications.AddRange(restoredClarifications);
        }
    }

    public static void PersistClarificationLedger(Guid runId, IDictionary<string, object?> metadata)
    {
        var clarifications = GetClarificationLedger(runId);
        HumanLoopLedgerSerializer.PersistClarificationLedger(clarifications, metadata, HumanLoopClarificationsMetadataKey);
    }

    public static void UpdateClarificationLedger(Guid runId, PendingHumanReview pendingReview, InteractionResponse response, string answer)
    {
        var clarifications = ClarificationsByRun.GetOrAdd(runId, static _ => []);
        var questionText = ResolveQuestionText(pendingReview);
        var summary = HumanLoopLedgerSerializer.BuildNormalizedSummary(response, answer);
        var resolutionStatus = HumanLoopResolutionInferrer.InferResolutionStatus(answer);
        var reAskPolicy = HumanLoopResolutionInferrer.ResolveReAskPolicy(resolutionStatus);
        var clarificationKeys = pendingReview.ClarificationKeys.Count > 0 ? pendingReview.ClarificationKeys : [HumanLoopResolutionInferrer.NormalizeQuestionFingerprint(questionText)];

        lock (clarifications)
        {
            foreach (var clarificationKey in clarificationKeys.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var activeEntries = clarifications.Where(existing => existing.IsActive && string.Equals(existing.ClarificationKey, clarificationKey, StringComparison.OrdinalIgnoreCase)).ToList();

                var newEntryId = Guid.NewGuid().ToString("N");
                foreach (var activeEntry in activeEntries)
                {
                    var index = clarifications.IndexOf(activeEntry);
                    if (index >= 0)
                    {
                        clarifications[index] = activeEntry with { ResolutionStatus = HumanLoopClarificationResolutionStatus.Superseded, SupersededBy = newEntryId, LastUpdatedAtTurn = pendingReview.Iteration, UpdatedAtUtc = response.RespondedAt };
                    }
                }

                clarifications.Add(
                    new HumanLoopClarificationState
                    {
                        EntryId = newEntryId,
                        ClarificationKey = clarificationKey,
                        QuestionText = questionText,
                        RawHumanAnswer = answer.Trim(),
                        NormalizedSummary = summary,
                        ResolutionStatus = resolutionStatus,
                        ReAskPolicy = reAskPolicy,
                        ResolvedAtTurn = pendingReview.Iteration,
                        LastUpdatedAtTurn = pendingReview.Iteration,
                        Supersedes = activeEntries.LastOrDefault()?.EntryId,
                        UpdatedAtUtc = response.RespondedAt,
                    }
                );
            }
        }
    }

    public static string? TryGetBlockedReAskReason(IReadOnlyList<HumanLoopClarificationState> clarifications, IReadOnlyList<string> clarificationKeys, IReadOnlyList<string> questionsToAsk, string? questionContext)
    {
        if (clarifications.Count == 0)
        {
            return null;
        }

        var activeClarifications = clarifications.Where(clarification => clarification.IsActive).ToList();
        if (activeClarifications.Count == 0)
        {
            return null;
        }

        var normalizedKeys = clarificationKeys.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var normalizedQuestionFingerprints = questionsToAsk.Where(value => !string.IsNullOrWhiteSpace(value)).Select(HumanLoopResolutionInferrer.NormalizeQuestionFingerprint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var matches = activeClarifications
            .Where(clarification =>
                normalizedKeys.Any(key => string.Equals(key, clarification.ClarificationKey, StringComparison.OrdinalIgnoreCase))
                || normalizedQuestionFingerprints.Any(fingerprint => string.Equals(fingerprint, HumanLoopResolutionInferrer.NormalizeQuestionFingerprint(clarification.QuestionText), StringComparison.OrdinalIgnoreCase))
            )
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var hasStrictPrecisionJustification = HumanLoopResolutionInferrer.HasStrictPrecisionJustification(questionContext);
        foreach (var clarification in matches)
        {
            if (clarification.ReAskPolicy == HumanLoopClarificationReAskPolicy.Allow)
            {
                continue;
            }

            if (clarification.ReAskPolicy == HumanLoopClarificationReAskPolicy.AllowIfToolRequiresExact && hasStrictPrecisionJustification)
            {
                continue;
            }

            return $"The clarification '{clarification.ClarificationKey}' is already resolved as {clarification.ResolutionStatus}: {clarification.NormalizedSummary}";
        }

        return null;
    }

    public static string BuildClarificationLedgerTraceSnapshot(IReadOnlyList<HumanLoopClarificationState> clarifications) => HumanLoopLedgerSerializer.BuildClarificationLedgerTraceSnapshot(clarifications);

    public static void ApplyHumanReviewResumeState(List<LlmChatMessage> messages, HumanReviewResumeState resumeState, ref string? finalResponse, Action<AgentTraceEvent>? appendExecutionTraceEvent = null)
    {
        if (resumeState.ExecutedToolCalls.Count > 0 && appendExecutionTraceEvent is not null)
        {
            appendExecutionTraceEvent(new AgentTraceEvent(0, "human_review_resume", null, [.. resumeState.ExecutedToolCalls]));
        }

        if (!string.IsNullOrWhiteSpace(resumeState.AssistantDraft) && !messages.Any(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) && string.Equals(message.Content?.Trim(), resumeState.AssistantDraft.Trim(), StringComparison.Ordinal)))
        {
            messages.Add(new LlmChatMessage("assistant", resumeState.AssistantDraft.Trim()));
        }

        foreach (var execution in resumeState.ExecutedToolCalls)
        {
            // A tool result is only valid right after the assistant message that made the call: providers
            // such as Anthropic reject a tool_result whose tool_use is not in the previous message. The
            // workflow resume path removes that assistant message (RemovePendingHumanReviewArtifacts) before
            // this runs, so put the call back — otherwise the very next LLM request fails with
            // "unexpected tool_use_id found in tool_result blocks". Left alone when it is still there.
            var callIsPresent = messages.Any(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && m.ToolCalls is { Count: > 0 }
                && m.ToolCalls.Any(call => string.Equals(call.Id, execution.ToolCallId, StringComparison.Ordinal)));
            if (!callIsPresent && !string.IsNullOrWhiteSpace(execution.ToolCallId))
            {
                messages.Add(new LlmChatMessage("assistant", null, ToolCalls: [new LlmToolCall(execution.ToolCallId, execution.ToolName, execution.ArgumentsJson)]));
            }

            messages.Add(new LlmChatMessage("tool", execution.ResultContent, ToolCallId: execution.ToolCallId));
        }

        if (!string.IsNullOrWhiteSpace(resumeState.ContinuationContext))
        {
            // Folded into the single leading system message rather than inserted as its own entry:
            // some chat templates (Jinja-based, e.g. Qwen's) reject any "system" message that isn't
            // the very first one in the list.
            var continuation = resumeState.ContinuationContext.Trim();
            if (messages.Count > 0 && string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                var existing = messages[0].Content ?? string.Empty;
                messages[0] = messages[0] with { Content = string.IsNullOrEmpty(existing) ? continuation : $"{existing}\n\n{continuation}" };
            }
            else
            {
                messages.Insert(0, new LlmChatMessage("system", continuation));
            }
        }

        if (!string.IsNullOrWhiteSpace(resumeState.FinalResponse))
        {
            finalResponse = resumeState.FinalResponse;
        }
    }

    public static void RemovePendingHumanReviewArtifacts(List<LlmChatMessage> messages, PendingHumanReview pendingReview)
    {
        if (messages.Count == 0)
        {
            return;
        }

        messages.RemoveAll(message =>
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            && message.ToolCalls is { Count: > 0 }
            && message.ToolCalls.Any(toolCall => !string.IsNullOrWhiteSpace(pendingReview.ToolCallId) && string.Equals(toolCall.Id, pendingReview.ToolCallId, StringComparison.OrdinalIgnoreCase))
        );
    }

    private static string ResolveQuestionText(PendingHumanReview pendingReview)
    {
        if (!string.IsNullOrWhiteSpace(pendingReview.Question))
        {
            return pendingReview.Question.Trim();
        }

        var questions = pendingReview.QuestionsToAsk.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList();
        return questions.Count == 0 ? "Clarification request" : string.Join(" / ", questions);
    }
}
