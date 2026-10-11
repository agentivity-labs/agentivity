using System.Text.Json;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Communication;

/// <summary>Sends an e-mail through an SMTP connection of the workspace.</summary>
public sealed class EmailNode : ToolNode
{
    private readonly IEmailSender _sender;

    public EmailNode(CredentialsService credentialsService, ILogger<EmailNode> logger)
        : this(new EmailService(credentialsService ?? throw new ArgumentNullException(nameof(credentialsService)), logger ?? throw new ArgumentNullException(nameof(logger)))) { }

    internal EmailNode(IEmailSender sender)
        : base("communication.email.send")
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));

        DefineMetadata(
            "Send Email",
            "Sends an e-mail through an SMTP connection: one or several recipients, optional copies, plain text or HTML. Can answer an e-mail that was read, so that both show as one conversation.",
            "communication",
            whenToUse: "To send a message to people outside the platform, or to answer an e-mail read with Read Emails. Not for asking a person something and waiting for the answer: use an interaction for that.",
            example: "to: {{read.from}}, subject: Re: {{read.subject}}, body: {{write.text}}, in_reply_to: {{read.message_id}}"
        );
        DefineEffects(NodeEffects.SendsMessage);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "smtp", "SMTP connection the e-mail is sent with", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("to", ArgumentType.Text, "Recipient address(es), separated by commas", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 20 }, required: true);
        DefineInput("cc", ArgumentType.Text, "Addresses that receive a copy", "", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 22 }, required: false);
        DefineInput("bcc", ArgumentType.Text, "Addresses that receive a copy the others do not see", "", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 24 }, required: false);
        DefineInput("reply_to", ArgumentType.Text, "Address the answers go to, when it is not the sender's", "", ui: new ArgumentUiMetadata { Group = "Recipients", Order = 30 }, required: false);
        DefineInput("subject", ArgumentType.Text, "Subject", ui: new ArgumentUiMetadata { Group = "Message", Order = 40 }, required: true);
        DefineInput(
            "body",
            ArgumentType.Text,
            "Body",
            "",
            ui: new ArgumentUiMetadata
            {
                Group = "Message",
                Order = 50,
                Multiline = true,
                Rows = 8,
            },
            required: true
        );
        DefineInput("is_html", ArgumentType.Boolean, "Whether the body is HTML", false, ui: new ArgumentUiMetadata { Group = "Message", Order = 60 }, required: false);
        DefineInput("in_reply_to", ArgumentType.Text, "The message_id of the e-mail this one answers (from Read Emails or Get Email): both then show as one conversation", "", ui: new ArgumentUiMetadata { Group = "Conversation", Order = 70 }, required: false);

        DefineOutput("sent", ArgumentType.Boolean, "Whether the e-mail was handed to the server");
        DefineOutput("message_id", ArgumentType.Text, "The id the e-mail was given");

        DefineIcon(NodeIcon.Material(0xe0be, "#06b6d4"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string messageId = await _sender
            .SendAsync(
                args.RequireText("credentials"),
                new OutgoingEmail(
                    To: args.RequireText("to"),
                    Subject: args.RequireText("subject"),
                    Body: args.GetText("body") ?? string.Empty,
                    IsHtml: args.GetBoolean("is_html") ?? false,
                    Cc: args.GetText("cc"),
                    Bcc: args.GetText("bcc"),
                    ReplyTo: args.GetText("reply_to"),
                    InReplyTo: args.GetText("in_reply_to")
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return NodeResult.Create().AddBoolean("sent", true).AddText("message_id", messageId);
    }
}

/// <summary>Reads the e-mails of a mailbox that match a few simple conditions, newest first.</summary>
public sealed class EmailReadNode : ToolNode
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEmailInbox _inbox;

    public EmailReadNode(CredentialsService credentialsService, ILogger<EmailReadNode> logger)
        : this(new EmailService(credentialsService ?? throw new ArgumentNullException(nameof(credentialsService)), logger ?? throw new ArgumentNullException(nameof(logger)))) { }

    internal EmailReadNode(IEmailInbox inbox)
        : base("communication.email.read")
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));

        DefineMetadata(
            "Read Emails",
            "Reads the e-mails of a mailbox (IMAP) that match: unread only, from someone, with words in the subject or the text, since a date. Newest first, with their text.",
            "communication",
            whenToUse: "To fetch what arrived in a mailbox: unread requests to sort, invoices received, answers to a campaign. It reads what is there when it runs: it does not wait for an e-mail to arrive.",
            example: "unread_only: true, subject_contains: invoice, limit: 10, mark_as_read: true"
        );
        DefineEffects(NodeEffects.ReadsExternal);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "imap", "Mailbox (IMAP connection) to read", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("folder", ArgumentType.Text, "Folder to read", "INBOX", ui: new ArgumentUiMetadata { Group = "Connection", Order = 12 }, required: false);
        DefineInput("unread_only", ArgumentType.Boolean, "Only the e-mails not read yet", true, ui: new ArgumentUiMetadata { Group = "Filter", Order = 20 }, required: false);
        DefineInput("from", ArgumentType.Text, "Only from an address or a name containing this", "", ui: new ArgumentUiMetadata { Group = "Filter", Order = 22 }, required: false);
        DefineInput("subject_contains", ArgumentType.Text, "Only with this in the subject", "", ui: new ArgumentUiMetadata { Group = "Filter", Order = 24 }, required: false);
        DefineInput("text_contains", ArgumentType.Text, "Only with this in the text", "", ui: new ArgumentUiMetadata { Group = "Filter", Order = 26 }, required: false);
        DefineInput("since", ArgumentType.Text, "Only received on or after this date (2026-01-31)", "", ui: new ArgumentUiMetadata { Group = "Filter", Order = 28 }, required: false);
        DefineInput("limit", ArgumentType.Number, "How many e-mails at most (newest first)", 20, ui: new ArgumentUiMetadata { Group = "Result", Order = 30 }, required: false);
        DefineInput("include_body", ArgumentType.Boolean, "Read the text of each e-mail (slower); otherwise only who, when, subject and a short preview", true, ui: new ArgumentUiMetadata { Group = "Result", Order = 32 }, required: false);
        DefineInput("max_body_chars", ArgumentType.Number, "The most characters kept of each text", 8000, ui: new ArgumentUiMetadata { Group = "Result", Order = 34 }, required: false);
        DefineInput("mark_as_read", ArgumentType.Boolean, "Mark the e-mails returned as read, so the next reading does not return them again", false, ui: new ArgumentUiMetadata { Group = "Result", Order = 36 }, required: false);

        DefineOutput("emails", ArgumentType.Text, "The e-mails as a JSON list: id, message_id, from, to, cc, subject, date, is_read, preview, body, attachments");
        DefineOutput("count", ArgumentType.Number, "How many e-mails were returned");
        DefineOutput("ids", ArgumentType.TextList, "The ids of the e-mails returned (for Get Email and Update Emails)");
        DefineOutput("id", ArgumentType.Text, "The id of the newest one, empty when there is none");
        DefineOutput("message_id", ArgumentType.Text, "The message_id of the newest one (to answer it with Send Email)");
        DefineOutput("from", ArgumentType.Text, "Who sent the newest one");
        DefineOutput("subject", ArgumentType.Text, "The subject of the newest one");
        DefineOutput("body", ArgumentType.Text, "The text of the newest one");

        DefineIcon(NodeIcon.Material(0xe156, "#06b6d4"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        DateTimeOffset? since = null;
        if (args.GetText("since")?.Trim() is { Length: > 0 } sinceText)
        {
            since = DateTimeOffset.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed
                : throw new InvalidOperationException($"'{sinceText}' is not a date. Write it as 2026-01-31.");
        }

        var query = new EmailQuery(
            UnreadOnly: args.GetBoolean("unread_only") ?? true,
            From: args.GetText("from"),
            SubjectContains: args.GetText("subject_contains"),
            TextContains: args.GetText("text_contains"),
            Since: since,
            Limit: args.GetInt("limit") ?? 20
        );
        bool includeBody = args.GetBoolean("include_body") ?? true;

        IReadOnlyList<EmailMessage> emails = await _inbox
            .SearchAsync(args.RequireText("credentials"), args.GetText("folder"), query, includeBody ? Math.Max(args.GetInt("max_body_chars") ?? 8000, 1) : 0, args.GetBoolean("mark_as_read") ?? false, cancellationToken)
            .ConfigureAwait(false);

        services.PublishStatus(emails.Count == 0 ? "No e-mail matches" : $"Read {emails.Count} e-mail{(emails.Count == 1 ? "" : "s")}");

        EmailMessage? newest = emails.Count > 0 ? emails[0] : null;
        return NodeResult
            .Create()
            .AddText("emails", JsonSerializer.Serialize(emails.Select(EmailMapping.ToJson), Json))
            .AddNumber("count", emails.Count)
            .AddTextList("ids", [.. emails.Select(e => e.Id)])
            .AddText("id", newest?.Id ?? string.Empty)
            .AddText("message_id", newest?.MessageId ?? string.Empty)
            .AddText("from", newest?.From ?? string.Empty)
            .AddText("subject", newest?.Subject ?? string.Empty)
            .AddText("body", newest?.Body ?? newest?.Preview ?? string.Empty);
    }
}

/// <summary>Reads one e-mail in full, by the id a listing gave.</summary>
public sealed class EmailGetNode : ToolNode
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEmailInbox _inbox;

    public EmailGetNode(CredentialsService credentialsService, ILogger<EmailGetNode> logger)
        : this(new EmailService(credentialsService ?? throw new ArgumentNullException(nameof(credentialsService)), logger ?? throw new ArgumentNullException(nameof(logger)))) { }

    internal EmailGetNode(IEmailInbox inbox)
        : base("communication.email.get")
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));

        DefineMetadata(
            "Get Email",
            "Reads one e-mail of a mailbox in full, by its id: who, when, subject, the whole text and the list of its attachments.",
            "communication",
            whenToUse: "After Read Emails listed e-mails without their text, or inside a loop over its ids, to work on one e-mail at a time.",
            example: "id: {{read.id}}"
        );
        DefineEffects(NodeEffects.ReadsExternal);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "imap", "Mailbox (IMAP connection) to read", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("folder", ArgumentType.Text, "Folder the e-mail is in", "INBOX", ui: new ArgumentUiMetadata { Group = "Connection", Order = 12 }, required: false);
        DefineInput("id", ArgumentType.Text, "The id of the e-mail, as Read Emails gave it", ui: new ArgumentUiMetadata { Group = "Email", Order = 20 }, required: true);
        DefineInput("max_body_chars", ArgumentType.Number, "The most characters kept of the text (0 = all of it)", 0, ui: new ArgumentUiMetadata { Group = "Email", Order = 22 }, required: false);
        DefineInput("mark_as_read", ArgumentType.Boolean, "Mark the e-mail as read", false, ui: new ArgumentUiMetadata { Group = "Email", Order = 24 }, required: false);

        DefineOutput("email", ArgumentType.Text, "The e-mail as a JSON object");
        DefineOutput("message_id", ArgumentType.Text, "Its message_id (to answer it with Send Email)");
        DefineOutput("from", ArgumentType.Text, "Who sent it");
        DefineOutput("reply_to", ArgumentType.Text, "The address to answer to (the sender's unless the e-mail says otherwise)");
        DefineOutput("to", ArgumentType.Text, "Who it was sent to");
        DefineOutput("subject", ArgumentType.Text, "Its subject");
        DefineOutput("date", ArgumentType.Text, "When it was sent");
        DefineOutput("body", ArgumentType.Text, "Its text");
        DefineOutput("attachments", ArgumentType.TextList, "The names of its attachments");

        DefineIcon(NodeIcon.Material(0xe158, "#06b6d4"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string id = args.RequireText("id");

        EmailMessage email =
            await _inbox.GetAsync(args.RequireText("credentials"), args.GetText("folder"), id, args.GetInt("max_body_chars") ?? 0, args.GetBoolean("mark_as_read") ?? false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"There is no e-mail '{id}' in this folder. An id is only valid in the folder it was listed from.");

        return NodeResult
            .Create()
            .AddText("email", JsonSerializer.Serialize(EmailMapping.ToJson(email), Json))
            .AddText("message_id", email.MessageId ?? string.Empty)
            .AddText("from", email.From)
            .AddText("reply_to", string.IsNullOrWhiteSpace(email.ReplyTo) ? email.From : email.ReplyTo)
            .AddText("to", email.To)
            .AddText("subject", email.Subject)
            .AddText("date", email.Date == default ? string.Empty : email.Date.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .AddText("body", email.Body ?? string.Empty)
            .AddTextList("attachments", [.. email.Attachments.Select(a => a.Name)]);
    }
}

/// <summary>Marks e-mails as read or unread, flags them, or files them in another folder. Never deletes.</summary>
public sealed class EmailUpdateNode : ToolNode
{
    private readonly IEmailInbox _inbox;

    public EmailUpdateNode(CredentialsService credentialsService, ILogger<EmailUpdateNode> logger)
        : this(new EmailService(credentialsService ?? throw new ArgumentNullException(nameof(credentialsService)), logger ?? throw new ArgumentNullException(nameof(logger)))) { }

    internal EmailUpdateNode(IEmailInbox inbox)
        : base("communication.email.update")
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));

        DefineMetadata(
            "Update Emails",
            "Marks e-mails of a mailbox as read or unread, flags or unflags them, or moves them to another folder. It never deletes an e-mail.",
            "communication",
            whenToUse: "Once e-mails have been handled: mark them read so they are not read again, or file them in a folder (Processed, Invoices…).",
            example: "ids: {{read.ids}}, action: move, target_folder: Processed"
        );
        DefineEffects(NodeEffects.WritesExternal);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineCredentialsInput("credentials", "imap", "Mailbox (IMAP connection)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("folder", ArgumentType.Text, "Folder the e-mails are in", "INBOX", ui: new ArgumentUiMetadata { Group = "Connection", Order = 12 }, required: false);
        DefineInput("ids", ArgumentType.TextList, "The ids of the e-mails, as Read Emails gave them", ui: new ArgumentUiMetadata { Group = "Emails", Order = 20 }, required: true);
        DefineChoice(
            "action",
            [("mark_read", "Mark as read"), ("mark_unread", "Mark as unread"), ("flag", "Flag"), ("unflag", "Remove the flag"), ("move", "Move to a folder")],
            "What to do with them",
            "mark_read",
            ui: new ArgumentUiMetadata { Group = "Action", Order = 30 },
            required: true
        );
        DefineInput("target_folder", ArgumentType.Text, "The folder to move them to (for Move to a folder); it must exist in the mailbox", "", ui: new ArgumentUiMetadata { Group = "Action", Order = 32 }, required: false);

        DefineOutput("updated", ArgumentType.Number, "How many e-mails were found and updated");

        DefineIcon(NodeIcon.Material(0xe151, "#06b6d4"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        IReadOnlyList<string> ids = args.GetStringList("ids", []);

        int updated = await _inbox
            .UpdateAsync(args.RequireText("credentials"), args.GetText("folder"), ids, EmailMapping.ParseUpdate(args.GetText("action") ?? "mark_read"), args.GetText("target_folder"), cancellationToken)
            .ConfigureAwait(false);

        return NodeResult.Create().AddNumber("updated", updated);
    }
}
