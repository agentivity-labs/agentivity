using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Moderation;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Agentic.Agent.Guardrails;

/// <summary>
/// Evaluates a list of guardrails for one prompt/response stage. <see cref="AgentGuardrailKind.Keyword"/>
/// stays exactly what it always was — an in-memory substring check, zero I/O, zero failure mode.
/// <see cref="AgentGuardrailKind.Moderation"/> and <see cref="AgentGuardrailKind.LlmJudge"/> make a
/// real call per evaluation and can genuinely fail (timeout, API error) — both **fail open**
/// (content passes through unchecked, a warning is logged) rather than blocking an entire agent
/// run over a transient outage of what is meant to be a safety net, not a hard dependency. This
/// was an explicit product decision (EPIC-0462, P1-8), not an oversight.
/// </summary>
public sealed class AgentGuardrailRuntime
{
    private readonly IModerationService? _moderationService;
    private readonly ILogger? _logger;

    public AgentGuardrailRuntime(IModerationService? moderationService = null, ILogger? logger = null)
    {
        _moderationService = moderationService;
        _logger = logger;
    }

    /// <summary>
    /// <paramref name="invokeLlm"/> is the agent's OWN connected LLM (same model/credentials it
    /// uses for its own reasoning) — required only for guardrails of kind
    /// <see cref="AgentGuardrailKind.LlmJudge"/>; pass null when the caller has no LLM access at
    /// this point (LlmJudge guardrails then fail open, same as a missing moderation service).
    /// </summary>
    public async ValueTask<AgentGuardrailApplication> ApplyAsync(
        IReadOnlyList<AgentGuardrailDefinition> guardrails,
        AgentGuardrailStage stage,
        string content,
        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>>? invokeLlm,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(content);

        var currentContent = content;
        var matches = new List<AgentGuardrailMatch>();
        var isBlocked = false;

        foreach (var guardrail in guardrails.Where(guardrail => guardrail.Stage == stage))
        {
            bool matched = await EvaluateAsync(guardrail, currentContent, invokeLlm, cancellationToken).ConfigureAwait(false);
            if (!matched)
            {
                continue;
            }

            matches.Add(new AgentGuardrailMatch(guardrail.GuardrailId, guardrail.Stage, guardrail.Action, string.IsNullOrWhiteSpace(guardrail.Message) ? DefaultMessage(guardrail) : guardrail.Message, guardrail.MatchText));

            switch (guardrail.Action)
            {
                case AgentGuardrailAction.Block:
                    isBlocked = true;
                    break;
                case AgentGuardrailAction.Rewrite when guardrail.Kind == AgentGuardrailKind.Keyword && !string.IsNullOrWhiteSpace(guardrail.ReplacementText):
                    // Rewrite only makes sense for a literal match — Moderation/LlmJudge guardrails
                    // know THAT content is flagged, not which exact substring to replace.
                    currentContent = Replace(currentContent, guardrail.MatchText, guardrail.ReplacementText!, guardrail.IgnoreCase);
                    break;
            }

            if (isBlocked)
            {
                break;
            }
        }

        return new AgentGuardrailApplication(currentContent, matches, isBlocked);
    }

    private async ValueTask<bool> EvaluateAsync(
        AgentGuardrailDefinition guardrail,
        string content,
        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>>? invokeLlm,
        CancellationToken cancellationToken
    ) =>
        guardrail.Kind switch
        {
            AgentGuardrailKind.Moderation => await EvaluateModerationAsync(guardrail, content, cancellationToken).ConfigureAwait(false),
            AgentGuardrailKind.LlmJudge => await EvaluateLlmJudgeAsync(guardrail, content, invokeLlm, cancellationToken).ConfigureAwait(false),
            _ => Contains(content, guardrail.MatchText, guardrail.IgnoreCase),
        };

    private async ValueTask<bool> EvaluateModerationAsync(AgentGuardrailDefinition guardrail, string content, CancellationToken cancellationToken)
    {
        if (_moderationService is null)
        {
            _logger?.LogWarning("Guardrail '{GuardrailId}' is a Moderation guardrail but no IModerationService is configured — failing open.", guardrail.GuardrailId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(guardrail.ModerationCredentialsId))
        {
            _logger?.LogWarning("Guardrail '{GuardrailId}' is missing its moderation credentials id — failing open.", guardrail.GuardrailId);
            return false;
        }

        try
        {
            ModerationResult result = await _moderationService.CheckAsync(guardrail.ModerationCredentialsId, content, cancellationToken).ConfigureAwait(false);
            return result.Flagged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Guardrail '{GuardrailId}' moderation check failed — failing open.", guardrail.GuardrailId);
            return false;
        }
    }

    private async ValueTask<bool> EvaluateLlmJudgeAsync(
        AgentGuardrailDefinition guardrail,
        string content,
        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>>? invokeLlm,
        CancellationToken cancellationToken
    )
    {
        if (invokeLlm is null)
        {
            _logger?.LogWarning("Guardrail '{GuardrailId}' is an LlmJudge guardrail but no LLM is available at this evaluation point — failing open.", guardrail.GuardrailId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(guardrail.JudgePrompt))
        {
            _logger?.LogWarning("Guardrail '{GuardrailId}' is missing its judge prompt — failing open.", guardrail.GuardrailId);
            return false;
        }

        try
        {
            string classificationPrompt = $"{guardrail.JudgePrompt}\n\n--- Content to classify ---\n{content}\n--- End of content ---\n\nRespond with exactly one word, nothing else: FLAGGED if the content violates the instruction above, or OK if it does not.";
            LlmConversationResponse response = await invokeLlm([new LlmChatMessage("user", classificationPrompt)], null, new LlmConversationOptions(Temperature: 0, MaxTokens: 10), cancellationToken).ConfigureAwait(false);
            return response.Content?.TrimStart().StartsWith("FLAGGED", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Guardrail '{GuardrailId}' LLM-judge check failed — failing open.", guardrail.GuardrailId);
            return false;
        }
    }

    private static string DefaultMessage(AgentGuardrailDefinition guardrail) =>
        guardrail.Kind switch
        {
            AgentGuardrailKind.Moderation => "Content flagged by moderation.",
            AgentGuardrailKind.LlmJudge => "Content flagged by LLM judge.",
            _ => $"Matched '{guardrail.MatchText}'.",
        };

    private static bool Contains(string content, string matchText, bool ignoreCase) => !string.IsNullOrEmpty(matchText) && content.Contains(matchText, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Replace(string content, string matchText, string replacementText, bool ignoreCase)
    {
        if (!ignoreCase)
        {
            return content.Replace(matchText, replacementText, StringComparison.Ordinal);
        }

        var index = content.IndexOf(matchText, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return content;
        }

        return string.Concat(content.AsSpan(0, index), replacementText, content.AsSpan(index + matchText.Length));
    }
}
