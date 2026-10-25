using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Interactions.Chat;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflow.AgUi;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Infrastructure.Execution.Executions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Interactions;

/// <summary>
/// Catalog node shell for the chat channel. Node structure (ports, inputs, metadata, icon)
/// is defined here; all channel logic lives in <see cref="ChatChannel"/>.
/// </summary>
public sealed class ChatChannelNode : ResourceNode, IInteractionChannel, IHumanInteractionChannel, IAgentHumanLoopCapabilityProvider, IAgUiCapabilityProvider
{
    private readonly ChatChannel _channel;

    public string ChannelType => _channel.ChannelType;

    public ChatChannelNode(IInteractionThreadStore chatStore, IRunEventBus? eventBus = null, IExecutionRecordStore? executionStore = null, IAgUiWidgetBundleRegistry? widgetRegistry = null, ILogger<ChatChannelNode>? logger = null)
        : base("ai.interactions.chat")
    {
        // Build the widget list from the active bundle registry so the LLM receives
        // a system-prompt listing of all renderable widgets for this channel.
        var widgets = widgetRegistry?
            .GetAll()
            .SelectMany(b => b.Widgets.Select(w => new ChatChannelWidget(w.TypeKey, w.Description, w.ParametersSchema)))
            .ToList();
        _channel = new ChatChannel(chatStore, eventBus, executionStore, widgets, widgetRegistry, logger);

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.InteractionChannel);

        DefineMetadata("Chat Channel", "Internal local chat channel for workflow conversations, human-in-the-loop responses, and AG-UI rich widgets.", "ai.interactions", whenToUse: "To let an AI Agent talk with the user in the app's chat: send messages, ask a question and wait for the reply, show rich cards. Plug it into the Human Loop Channel of an AI Agent, or use it with an interaction step.");
        DefineEffects(NodeEffects.WritesInternal);
        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Channel", ResourcePortCategory.InteractionChannel, maxConnections: -1);

        DefineInput("default_thread_title", ArgumentType.Text, "Default thread title for newly created workflow conversations", "Main", ui: new ArgumentUiMetadata { Group = "Channel", Order = 20 }, required: false);
        DefineInput("workflow_author_name", ArgumentType.Text, "Display name used for workflow-authored messages", "Workflow", ui: new ArgumentUiMetadata { Group = "Display", Order = 30 }, required: false);
        DefineInput("respondent", ArgumentType.Text, "Expected respondent name for traceability", ui: new ArgumentUiMetadata { Group = "Display", Order = 40 }, required: false);
        DefineInput("timeout_minutes", ArgumentType.Number, "Response timeout in minutes (0 = no timeout)", 0, ui: new ArgumentUiMetadata { Group = "Settings", Order = 50 }, required: false);
        DefineInput("widget_bundles", ArgumentType.TextList, "Platform widget bundle IDs to activate for this run.", "[\"agentivity_artifacts\"]", ui: new ArgumentUiMetadata { Group = "Widgets", Order = 60 }, required: false);
        DefineInput("custom_widgets", ArgumentType.Structured, "Application-specific widget definitions the LLM can invoke as tool calls.", "[]", ui: new ArgumentUiMetadata { Group = "Widgets", Order = 70 }, required: false);

        DefineIcon(NodeIcon.Material(0xe0b9, "#5b55e8"));
    }

    public IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations() => _channel.GetSupportedOperations();

    public AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true) => _channel.GetAgentHumanLoopCapabilities(channel, allowsAskMode);

    public ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default) => _channel.DispatchAsync(channel, request, cancellationToken);

    public ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default) => _channel.ExecuteAsync(channel, request, cancellationToken);

    public ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default) => _channel.TryResolveInboundAsync(request, inboundMessage, cancellationToken);

    public IReadOnlyList<string> GetActiveBundleIds(IResourceBindingReader binding) => _channel.GetActiveBundleIds(binding);

    public IReadOnlyList<AgUiCustomWidgetDefinition> GetCustomWidgets(IResourceBindingReader binding) => _channel.GetCustomWidgets(binding);
}
