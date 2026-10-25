namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Resolves the final response text an agent execution surfaces to its caller —
/// shared between the workflow path (<c>AiNode.ExecuteAsync</c>) and the standalone path
/// (<see cref="Agentivity.Infrastructure.Agentic.Composables.AgentEntityRunner"/>).
/// <para>
/// Before this helper existed, <c>AiNode</c> alone:
/// <list type="bullet">
///   <item>threw when the runtime produced no response AND the finish reason wasn't <c>max_iterations</c>
///   (an empty/blank response is only tolerated when the agent simply ran out of budget);</item>
///   <item>substituted a friendly, budget-aware fallback message — and published a status update —
///   when the agent DID hit its iteration limit without producing anything.</item>
/// </list>
/// <c>AgentEntityRunner</c> had neither: it returned whatever the mode runtime produced,
/// including a possibly blank response on max-iterations exhaustion. This helper makes that
/// finalization rule the single source of truth for both paths
/// (characterized in <c>AiNodeCharacterizationTests.AiNode_ExecuteAsync_ClampsMaxIterations_ToOneAndThirtyTwo</c>
/// and mirrored in <c>AgentEntityRunnerCharacterizationTests</c>).
/// </para>
/// </summary>
public static class AgentResponseFinalization
{
    /// <summary>
    /// Resolves the final response, applying the max-iterations fallback when needed.
    /// </summary>
    /// <param name="finalResponse">The raw response produced by the mode runtime (may be null/blank).</param>
    /// <param name="finishReason">The runtime's reported finish reason.</param>
    /// <param name="maxIterations">The configured (already-clamped) iteration budget, for the fallback message.</param>
    /// <param name="publishStatus">Optional status sink — invoked only when the fallback substitution happens.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the response is blank for any reason OTHER than exhausting the iteration budget —
    /// an empty response is only ever tolerable as a budget-exhaustion symptom.
    /// </exception>
    public static string Resolve(string? finalResponse, string finishReason, int? maxIterations, Action<string>? publishStatus = null)
    {
        if (string.IsNullOrWhiteSpace(finalResponse) && !string.Equals(finishReason, AgenticFinishReason.MaxIterations, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI Node did not produce a final response.");
        }

        if (string.IsNullOrWhiteSpace(finalResponse))
        {
            var limitDisplay = maxIterations.HasValue ? maxIterations.Value.ToString() : "default";
            publishStatus?.Invoke($"AI Node reached the maximum iteration limit ({limitDisplay}) without producing a final response.");
            return $"The agent reached its iteration limit ({limitDisplay}) without completing the task. Please try again with a simpler request or increase the iteration budget.";
        }

        return finalResponse;
    }
}
