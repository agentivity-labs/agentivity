using Agentivity.Infrastructure.Interactions;

namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// <see cref="IInboundMessageResolver"/> backed by the workflow channel registry.
/// Delegates to <see cref="HumanInteractionChannelDispatcher.TryResolveInboundAsync"/>
/// which dispatches to the channel node that owns the request.
/// </summary>
public sealed class HumanInteractionChannelResolver : IInboundMessageResolver
{
    public ValueTask<InteractionResponse?> TryResolveAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken ct) => HumanInteractionChannelDispatcher.TryResolveInboundAsync(request, inboundMessage, ct);
}
