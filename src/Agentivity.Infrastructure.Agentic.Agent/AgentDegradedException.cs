namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Thrown by the agent infrastructure when the agent cannot complete normally but
/// the failure is not a programming error — e.g. a missing tool, a technical tool
/// exception, or a hallucinated tool name.
///
/// Runtimes (ReAct, Plan &amp; Execute) catch this exception and convert it to an
/// <see cref="AgentRunResult"/> with <see cref="AgentRunResult.IsDegraded"/> = <see langword="true"/>
/// so that the end user receives a readable explanation instead of a raw exception.
/// </summary>
public sealed class AgentDegradedException : Exception
{
    /// <summary>
    /// One of the degraded <see cref="AgenticFinishReason"/> constants
    /// (e.g. <see cref="AgenticFinishReason.ToolNotAvailable"/>).
    /// </summary>
    public string FinishReason { get; }

    /// <summary>
    /// A clear, actionable user-facing message that describes what went wrong and
    /// what the user can do to proceed.
    /// </summary>
    public string UserFacingMessage { get; }

    public AgentDegradedException(string finishReason, string userFacingMessage)
        : base($"[{finishReason}] {userFacingMessage}")
    {
        FinishReason = finishReason;
        UserFacingMessage = userFacingMessage;
    }

    public AgentDegradedException(string finishReason, string userFacingMessage, Exception innerException)
        : base($"[{finishReason}] {userFacingMessage}", innerException)
    {
        FinishReason = finishReason;
        UserFacingMessage = userFacingMessage;
    }
}
