namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Well-known finish reason constants for <see cref="AgentRunResult.FinishReason"/>.
///
/// Using string constants (not an enum) preserves backward compatibility with callers
/// that already read FinishReason as a string, and with LLM provider finish reasons
/// that flow through the same field (e.g. "stop", "tool_calls", "length").
///
/// Terminal paths produced by the agent infrastructure:
/// - <see cref="FinalAnswer"/>           — normal completion with a response
/// - <see cref="MaxIterations"/>         — iteration budget exhausted without a final response
/// - <see cref="GuardrailBlocked"/>      — a guardrail blocked the output (throws, not returned)
/// - <see cref="ToolNotAvailable"/>      — tool referenced by the LLM is not registered
/// - <see cref="ToolCallFailed"/>        — tool available but threw a technical exception
/// - <see cref="ToolNotApplicable"/>     — tool returned empty / unsupported result
/// - <see cref="HallucinatedToolName"/>  — LLM requested a tool name absent from the provided list
/// - <see cref="HallucinatedParameters"/> — LLM invented values for parameters absent from the conversation
/// - <see cref="PlanInvalid"/>           — plan could not be parsed or repaired (P&amp;E)
/// - <see cref="CircularReplan"/>        — identical plan detected consecutively (P&amp;E)
///
/// LLM provider finish reasons (flow-through, not produced by infrastructure):
/// - "stop"        — model chose to stop
/// - "tool_calls"  — model requested tool calls (intermediate, not a terminal result)
/// - "length"      — max tokens reached on the model side
/// </summary>
public static class AgenticFinishReason
{
    /// <summary>
    /// The agent produced a final response within the iteration budget.
    /// </summary>
    public const string FinalAnswer = "final_answer";

    /// <summary>
    /// The agent exhausted the maximum iteration budget without producing a final response.
    /// The <see cref="AgentRunResult.Response"/> will be empty.
    /// Callers should treat this as a degraded result and apply a fallback strategy.
    /// </summary>
    public const string MaxIterations = "max_iterations_reached";

    /// <summary>
    /// A guardrail blocked the final response. This finish reason is not returned —
    /// <see cref="System.InvalidOperationException"/> is thrown instead, which is the
    /// correct behavior for a security stop. This constant is provided for documentation
    /// and test assertion purposes only.
    /// </summary>
    public const string GuardrailBlocked = "guardrail_blocked";

    /// <summary>
    /// The tool referenced by the LLM is not registered in the agent's tool list.
    /// The agent returns a degraded result with a user-readable explanation.
    /// </summary>
    public const string ToolNotAvailable = "tool_not_available";

    /// <summary>
    /// A tool was available but threw a technical exception during execution.
    /// The agent returns a degraded result with a sanitised message.
    /// </summary>
    public const string ToolCallFailed = "tool_call_failed";

    /// <summary>
    /// A tool was available but returned an empty or unsupported result.
    /// The agent returns a degraded result explaining that the information is unavailable.
    /// </summary>
    public const string ToolNotApplicable = "tool_not_applicable";

    /// <summary>
    /// The LLM requested a tool whose name was not present in the list of tools
    /// provided to it, indicating prompt hallucination of a tool name.
    /// </summary>
    public const string HallucinatedToolName = "hallucinated_tool_name";

    /// <summary>
    /// The LLM invented values for required parameters that were absent from the
    /// conversation, rather than asking the user for the missing information.
    /// </summary>
    public const string HallucinatedParameters = "hallucinated_parameters";

    /// <summary>
    /// The Plan &amp; Execute planner could not produce a valid execution plan even after
    /// repair attempts. The agent returns a degraded result asking the user to rephrase.
    /// </summary>
    public const string PlanInvalid = "plan_invalid";

    /// <summary>
    /// The Plan &amp; Execute join produced a plan structurally identical to the previous
    /// one for N consecutive rounds. The agent terminates with the best partial response.
    /// </summary>
    public const string CircularReplan = "circular_replan";

    /// <summary>
    /// The join determined that the request lacks sufficient context or constraints
    /// for the agent to act autonomously. The agent surfaces a clarification question.
    /// </summary>
    public const string NeedsUserClarification = "needs_user_clarification";

    /// <summary>
    /// The join determined that the task cannot be completed given the current tools,
    /// constraints, or available information, regardless of replanning.
    /// </summary>
    public const string CannotComplete = "cannot_complete";
}
