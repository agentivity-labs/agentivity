namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Generic interaction bridge — channel-agnostic entry point for resolving inbound messages
/// to pending <see cref="InteractionRequest"/>s and resuming the owning run (workflow, team,
/// standalone agent, …) via <see cref="DurableRunService"/>, the single resume path.
/// </summary>
public interface IInteractionBridge
{
    ValueTask<IReadOnlyList<InteractionRequest>> ListPendingRequestsAsync(Guid? runId = null, string? channelType = null, CancellationToken ct = default);

    ValueTask<InteractionRequest?> GetRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>Saves <paramref name="response"/> then resumes the owning run via <see cref="DurableRunService"/>.</summary>
    ValueTask<InteractionResumeResult> ResumeAsync(Guid requestId, InteractionResponse response, CancellationToken ct = default);

    /// <summary>
    /// Attempts to match <paramref name="inboundMessage"/> to a pending request and resume the run.
    /// Returns <c>null</c> when no matching pending request exists (not an error).
    /// </summary>
    ValueTask<InteractionResumeResult?> TryResumeInboundAsync(InteractionChannelInboundMessage inboundMessage, Guid? explicitRequestId = null, Guid? runId = null, CancellationToken ct = default);
}

public sealed record InteractionResumeResult(bool IsSuccess, string? Error, Guid RunId, Guid RequestId);

public class InteractionBridgeNotConfiguredException(string message) : InvalidOperationException(message);
