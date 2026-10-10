using System.Text;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Pure compaction logic shared by every trigger (per-turn <c>SummaryStep</c>, team-handoff),
/// plus the raw read/persist bounding every session goes through regardless of trigger.
/// <para>
/// Deliberately no <see cref="ILlmProvider"/> dependency on the type itself — <see cref="CompactAsync"/>
/// takes an already-resolved provider/binding as parameters instead, so this stays callable from
/// both an <c>AgentStep</c> (agent's own LLM) and directly from a team orchestrator (a borrowed
/// member's LLM, no <c>AgentStepContext</c> available) — see
/// <c>.claude/EPIC-session-summary-unification.md</c>.
/// </para>
/// </summary>
public static class AgentSessionCompaction
{
    public const string AgentWorkspaceMetadataKey = AgentSessionWorkspace.MetadataKey;

    /// <summary>
    /// Token budget for the summary text itself. Raised from an original 800 after production
    /// evidence (21 of 30 compaction calls on one real multi-agent team run) showed it being hit
    /// routinely — compression is instructed in <see cref="BuildSummaryPrompt"/>, but a team run
    /// with several substantial specialist reports genuinely needs more room to distill than a
    /// single short exchange does; 800 was tuned for the latter, not the former.
    /// </summary>
    public const int DefaultSummaryMaxTokens = 8000;

    // ── Raw read/persist bounding — always applied, independent of compaction_mode ────────────

    /// <summary>
    /// Trims to at most <c>max_messages</c> (binding, default 50) on read, pairing-safe (never
    /// drops a tool_use without its tool_result, or vice versa).
    /// </summary>
    public static AgentSessionState NormalizeForRead(ResourceBinding binding, AgentSessionState session)
    {
        var maxMessages = Math.Max(1, binding.GetInt("max_messages") ?? 50);
        if (session.Messages.Count <= maxMessages)
            return CloneSession(session);

        var dropCount = ResolveSafeDropCount(session.Messages, maxMessages);
        return CloneSession(session) with { Messages = session.Messages.Skip(dropCount).Select(CloneMessage).ToList() };
    }

    /// <summary>
    /// Same bound as <see cref="NormalizeForRead"/>, plus a refresh of the agent-workspace
    /// metadata section. The rolling LLM summary is <b>not</b> produced here — that's an explicit,
    /// threshold-gated trigger (<c>SummaryStep</c>/team handoff calling <see cref="CompactAsync"/>),
    /// never an implicit side effect of persisting.
    /// </summary>
    public static AgentSessionState NormalizeForPersistence(ResourceBinding binding, AgentSessionState session)
    {
        var normalized = NormalizeForRead(binding, session);
        var metadata = new Dictionary<string, object?>(normalized.Metadata, StringComparer.OrdinalIgnoreCase);
        AgentSessionWorkspace.RefreshMetadata(metadata, normalized.Messages);
        return normalized with { Metadata = metadata };
    }

    public static string? BuildAgentWorkspacePrompt(IReadOnlyDictionary<string, object?> metadata, IReadOnlyList<string>? additionalRules = null) => AgentSessionWorkspace.BuildPrompt(metadata, additionalRules);

    public static void RefreshAgentWorkspaceMetadata(IDictionary<string, object?> metadata, IReadOnlyList<LlmChatMessage> messages, AgentWorkspacePolicyOptions? options = null) => AgentSessionWorkspace.RefreshMetadata(metadata, messages, options);

    // ── Compaction recipe — shared by SummaryStep (agent, per-turn) and the team-handoff trigger ──

    public sealed record CompactionOptions(int SummaryMaxTokens = DefaultSummaryMaxTokens, double? Temperature = null);

    /// <summary>
    /// Updates the rolling summary with <paramref name="delta"/> — the exact, already
    /// pairing-safe slice the caller decided to consume (see <see cref="ResolveSafeConsumeCount"/>).
    /// Makes the actual LLM call itself: no <c>AgentStep</c>/<c>AgentStepContext</c> required, so a
    /// team orchestrator can call this directly with a borrowed member's LLM. Falls back to the
    /// existing summary unchanged if the call fails — never blocks the caller's turn on this.
    /// </summary>
    public static async ValueTask<string> CompactAsync(ILlmProvider provider, ResourceBinding binding, string? existingSummary, IReadOnlyList<LlmChatMessage> delta, CompactionOptions options, CancellationToken ct)
    {
        var prompt = BuildSummaryPrompt(existingSummary, delta);
        var messages = new List<LlmChatMessage> { new("user", prompt) };
        var llmOptions = new LlmConversationOptions(Temperature: options.Temperature, MaxTokens: options.SummaryMaxTokens, ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false);

        try
        {
            var result = await provider.CompleteConversationAsync(binding, messages, null, llmOptions, ct).ConfigureAwait(false);
            return result.IsSuccess && result.Value is { } response && !string.IsNullOrWhiteSpace(response.Content)
                ? response.Content.Trim()
                : existingSummary ?? string.Empty;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Never blocks the caller's turn on this — a compaction failure (network error,
            // provider outage) must not derail the actual conversation, just leave the summary
            // stale until the next successful call.
            return existingSummary ?? string.Empty;
        }
    }

    public static string BuildSummaryPrompt(string? existingSummary, IReadOnlyList<LlmChatMessage> delta)
    {
        var existingTranscript = string.IsNullOrWhiteSpace(existingSummary) ? "(aucun résumé pour l'instant)" : existingSummary;
        return $"""
            Résumé actuel de la conversation :
            {existingTranscript}

            Nouveaux échanges à intégrer :
            {DescribeDelta(delta)}

            Mets à jour le résumé pour qu'il inclue ces nouveaux échanges, en conservant ce qui reste pertinent du résumé actuel. Le résumé doit toujours porter, en plus des faits : (1) le but ou la demande d'ensemble (ce que la personne cherche à accomplir, pas seulement ce qu'elle a dit), et (2) où en est la situation maintenant. Préserve explicitement les faits courts et discrets (identifiants, montants, noms, dates, statuts) — ne les résume jamais de façon vague, une information précise perdue ne peut pas être reconstituée ensuite. En revanche, si les nouveaux échanges contiennent un livrable long (article, rapport, document...), ne cherche pas à en reproduire ou détailler le contenu ici — note seulement son sujet et son existence en une phrase, ce contenu reste disponible intégralement ailleurs. Réponds uniquement avec le texte du résumé mis à jour, sans préambule ni commentaire.
            """;
    }

    /// <summary>Rough token estimate — <c>characters / 4</c>, no tokenizer.</summary>
    public static int EstimateTokens(IEnumerable<LlmChatMessage> messages) =>
        messages.Sum(m => (m.Content?.Length ?? 0) + (m.ToolCalls?.Sum(tc => tc.ArgumentsJson.Length) ?? 0)) / 4;

    /// <summary>
    /// Walks <paramref name="delta"/> from the start, tracking tool_use ids opened by a message
    /// with <see cref="LlmChatMessage.ToolCalls"/> until a later message's
    /// <see cref="LlmChatMessage.ToolCallId"/> resolves them (a <see cref="LlmChatMessage.IsPlaceholder"/>
    /// entry never counts as the resolution — see <c>SummaryStep</c>'s original design notes).
    /// Returns the largest prefix length that both (a) ends with zero ids still pending and (b)
    /// brings the remaining (unconsumed) tokens back under <paramref name="tokenThreshold"/> — or,
    /// if that's never satisfied at a safe boundary, the last safe boundary found regardless (never
    /// an unsafe one; can return 0 if the delta opens with a tool_use unresolved anywhere in it).
    /// <para>
    /// A boundary is only usable when what remains after it <em>starts with a person's message</em>: the
    /// conversation sent to the model is the summary (folded into the system prompt) plus the remaining
    /// messages, and a provider refuses one with no message at all, or one that opens on an assistant turn
    /// (Anthropic: "at least one message is required" / "first message must use the user role"). An agent that
    /// reads several pages in a row has a single request followed by tool exchanges, so the only way "what
    /// remains" fell under the threshold was to consume everything — leaving the next call with nothing to send.
    /// In that case there is no valid boundary and nothing is consumed: a long tool exchange is kept whole.
    /// </para>
    /// </summary>
    public static int ResolveSafeConsumeCount(IReadOnlyList<LlmChatMessage> delta, int tokenThreshold)
    {
        var pendingToolUseIds = new HashSet<string>();
        var remaining = EstimateTokens(delta);
        var safeCount = 0;

        for (var i = 0; i < delta.Count; i++)
        {
            var message = delta[i];
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                    pendingToolUseIds.Add(call.Id);
            }
            else if (!message.IsPlaceholder && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                pendingToolUseIds.Remove(message.ToolCallId);
            }

            remaining -= EstimateTokens([message]);

            if (pendingToolUseIds.Count == 0 && i + 1 < delta.Count && StartsATurn(delta[i + 1]))
            {
                safeCount = i + 1;
                if (remaining <= tokenThreshold)
                    break;
            }
        }

        return safeCount;
    }

    /// <summary>A person's own message — not a tool result, not an assistant turn, not a placeholder.</summary>
    private static bool StartsATurn(LlmChatMessage message) =>
        string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(message.ToolCallId) && !message.IsPlaceholder;

    /// <summary>
    /// Same pairing-safety walk as <see cref="ResolveSafeConsumeCount"/>, but stops as soon as the
    /// remaining (unconsumed) message <b>count</b> — not token estimate — is at or under
    /// <paramref name="keepCount"/>. Used by <see cref="NormalizeForRead"/>'s raw trim.
    /// </summary>
    private static int ResolveSafeDropCount(IReadOnlyList<LlmChatMessage> messages, int keepCount)
    {
        var pendingToolUseIds = new HashSet<string>();
        var safeCount = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                    pendingToolUseIds.Add(call.Id);
            }
            else if (!message.IsPlaceholder && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                pendingToolUseIds.Remove(message.ToolCallId);
            }

            if (pendingToolUseIds.Count == 0)
            {
                safeCount = i + 1;
                if (messages.Count - safeCount <= keepCount)
                    break;
            }
        }

        return safeCount;
    }

    private static string DescribeDelta(IReadOnlyList<LlmChatMessage> delta)
    {
        var builder = new StringBuilder();
        foreach (var message in delta)
        {
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                    builder.AppendLine($"{message.Role} a appelé '{call.Name}' avec : {call.ArgumentsJson}");
            }
            else if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                builder.AppendLine($"Résultat de l'outil : {message.Content}");
            }
            else if (!string.IsNullOrWhiteSpace(message.Content))
            {
                builder.AppendLine($"{message.Role}: {message.Content}");
            }
        }
        return builder.ToString();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static AgentSessionState CloneSession(AgentSessionState session) =>
        session with
        {
            Messages = session.Messages.Select(CloneMessage).ToList(),
            ToolState = session.ToolState.ToDictionary(kvp => kvp.Key, kvp => (JsonObject)kvp.Value.DeepClone(), StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?>(session.Metadata, StringComparer.OrdinalIgnoreCase),
        };

    private static LlmChatMessage CloneMessage(LlmChatMessage message) => new(message.Role, message.Content, message.ToolCallId, message.ToolCalls?.Select(toolCall => new LlmToolCall(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson)).ToList());
}
