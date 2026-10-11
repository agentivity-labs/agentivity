using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Interactions.Forms;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Interactions;

/// <summary>
/// Catalog node shell for the forms channel. Node structure (ports, inputs, metadata, icon)
/// is defined here; all channel logic lives in <see cref="FormsChannel"/>.
/// </summary>
public sealed class FormsChannelNode : ResourceNode, IInteractionChannel, IHumanInteractionChannel, IAgentHumanLoopCapabilityProvider
{
    private readonly FormsChannel _channel;

    public string ChannelType => _channel.ChannelType;

    public FormsChannelNode(IInteractionThreadStore? threadStore = null, IInteractionRequestStore? requestStore = null, IRunEventBus? eventBus = null)
        : base("ai.interactions.forms")
    {
        _channel = new FormsChannel(threadStore, requestStore, eventBus);

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.InteractionChannel);

        DefineMetadata("Forms Channel", "Web form-based interaction channel for collecting user input.", "ai.interactions");
        DefineEffects(NodeEffects.SendsMessage);
        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Channel", ResourcePortCategory.InteractionChannel, maxConnections: -1);

        DefineInput("respondent", ArgumentType.Text, "Name or email of the person responding (for traceability in logs)", ui: new ArgumentUiMetadata { Group = "Channel", Order = 20 }, required: false);
        DefineInput("timeout_minutes", ArgumentType.Number, "Response timeout in minutes (0 = no timeout)", 0, ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 }, required: false);

        DefineIcon(NodeIcon.Material(0xf1c1, "#5b55e8"));
    }

    public IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations() => _channel.GetSupportedOperations();

    public AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true) => _channel.GetAgentHumanLoopCapabilities(channel, allowsAskMode);

    public ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default) => _channel.DispatchAsync(channel, request, cancellationToken);

    public ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default) => _channel.ExecuteAsync(channel, request, cancellationToken);

    public ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default) => _channel.TryResolveInboundAsync(request, inboundMessage, cancellationToken);
}
