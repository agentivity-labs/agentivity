using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Communication;

public sealed class EmailNode : ToolNode
{
    private readonly EmailService _emailService;

    public EmailNode(CredentialsService credentialsService, ILogger<EmailNode> logger)
        : base("communication.email.send")
    {
        ArgumentNullException.ThrowIfNull(credentialsService);
        _emailService = new EmailService(credentialsService, logger ?? throw new ArgumentNullException(nameof(logger)));

        DefineMetadata("Send Email", "Sends an email as a standard workflow action using shared SMTP communication logic.", "communication");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "smtp", "SMTP credentials for sending emails", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput("to", ArgumentType.Text, "Recipient email address(es)", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 20 });
        DefineInput("reply_to", ArgumentType.Text, "Reply-to address for responses", "", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 30 });
        DefineInput("subject", ArgumentType.Text, "Email subject", ui: new ArgumentUiMetadata { Group = "Message", Order = 40 });
        DefineInput(
            "body",
            ArgumentType.Text,
            "Email body",
            "",
            ui: new ArgumentUiMetadata
            {
                Group = "Message",
                Order = 50,
                Multiline = true,
                Rows = 8,
            }
        );
        DefineInput("is_html", ArgumentType.Boolean, "Whether the email body is HTML", false, ui: new ArgumentUiMetadata { Group = "Message", Order = 60 });

        DefineOutput("sent", ArgumentType.Boolean, "Whether the email was sent");

        DefineIcon(NodeIcon.Material(0xe0be, "#06b6d4"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        await _emailService
            .SendAsync(credentialsId: args.RequireText("credentials"), recipients: args.RequireText("to"), replyTo: args.GetText("reply_to"), subject: args.RequireText("subject"), body: args.GetText("body") ?? string.Empty, isHtml: args.GetBoolean("is_html") ?? false, cancellationToken)
            .ConfigureAwait(false);

        return NodeResult.Create().AddBoolean("sent", true);
    }
}
