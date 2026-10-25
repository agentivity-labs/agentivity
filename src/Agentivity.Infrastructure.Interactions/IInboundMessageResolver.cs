namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Resolves an inbound channel message to an <see cref="InteractionResponse"/> for a given
/// pending <see cref="InteractionRequest"/>. Registered per channel kind (chat, email, Slack, forms, …).
/// </summary>
public interface IInboundMessageResolver
{
    ValueTask<InteractionResponse?> TryResolveAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken ct);
}
