using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Interactions;

/// <summary>
/// Email channel resource node for email-based user interactions.
/// </summary>
public sealed class EmailChannelNode : ResourceNode, IInteractionChannel, IHumanInteractionChannel, IAgentHumanLoopCapabilityProvider
{
    private readonly EmailService _emailService;
    private static readonly HumanInteractionOperation[] SupportedOperations = [HumanInteractionOperation.Send];

    public string ChannelType => "email";

    public EmailChannelNode(CredentialsService credentialsService, ILogger<EmailChannelNode> logger)
        : base("ai.interactions.email")
    {
        ArgumentNullException.ThrowIfNull(credentialsService);
        _emailService = new EmailService(credentialsService, logger ?? throw new ArgumentNullException(nameof(logger)));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.InteractionChannel);

        DefineMetadata("Email Channel", "Email-based interaction channel for delivering requests and collecting responses.", "ai.interactions", whenToUse: "To tell someone by e-mail that a workflow needs their answer: the e-mail only notifies, the answer is still given in the app. An AI Agent cannot ask questions through it: use the Chat, Forms or Slack channel for that.");
        DefineEffects(NodeEffects.SendsMessage);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Channel", ResourcePortCategory.InteractionChannel, maxConnections: -1);

        DefineCredentialsInput("credentials", "smtp", "SMTP credentials for sending emails", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("to", ArgumentType.Text, "Recipient email address(es)", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 30 }, required: true);
        DefineInput("reply_to", ArgumentType.Text, "Reply-to address for responses", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 40 }, required: false);
        DefineInput("timeout_minutes", ArgumentType.Number, "Response timeout in minutes (0 = no timeout)", 1440, ui: new ArgumentUiMetadata { Group = "Settings", Order = 50 }, required: false);

        DefineIcon(NodeIcon.Material(0xe0be, "#5b55e8")); // email icon
    }

    public async ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        string body = EmailFormatting.BuildInteractionMessage(request);
        string subject = EmailFormatting.BuildInteractionSubject(request);

        await _emailService.SendAsync(credentialsId: channel.Properties.RequireString("credentials"), recipients: channel.Properties.RequireString("to"), replyTo: channel.Properties.GetString("reply_to"), subject, body, isHtml: false, cancellationToken).ConfigureAwait(false);

        return new InteractionChannelDispatchResult(true, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["email.credentials_id"] = channel.Properties.RequireString("credentials"), ["email.recipients"] = channel.Properties.RequireString("to") });
    }

    public ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default) => ValueTask.FromResult<InteractionResponse?>(null);

    public IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations() => SupportedOperations;

    public AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true) => AgentHumanLoopCapabilities.None; // Keep email as a non-agentic HIL channel

    public async ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation != HumanInteractionOperation.Send)
        {
            throw new InvalidOperationException($"Email channel does not support human interaction operation '{request.Operation}'.");
        }

        string body = string.IsNullOrWhiteSpace(request.Message) ? request.Description ?? string.Empty : request.Message;
        string subject = string.IsNullOrWhiteSpace(request.Title) ? "Human interaction" : request.Title;

        await _emailService.SendAsync(credentialsId: channel.Properties.RequireString("credentials"), recipients: channel.Properties.RequireString("to"), replyTo: channel.Properties.GetString("reply_to"), subject, body, isHtml: false, cancellationToken).ConfigureAwait(false);

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["email.credentials_id"] = channel.Properties.RequireString("credentials"), ["email.recipients"] = channel.Properties.RequireString("to") };

        return HumanInteractionExecutionResult.Completed(
            new InteractionResponse
            {
                RequestId = request.Id,
                Outcome = InteractionOutcome.Submitted,
                Success = true,
                Channel = channel.ChannelType,
                Metadata = metadata,
            },
            metadata
        );
    }
}
