using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Communication;

/// <summary>
/// Gives an agent a mailbox: it can look for e-mails, read one, answer it, write a new one, and file what it handled. A
/// single self-contained node: pick the mailbox to read (an IMAP connection) and the connection to send with (SMTP) on the
/// node itself, and wire its <c>resource</c> output into an <c>ai.node</c>'s <c>tools</c> port.
/// <para>
/// What the agent may do is decided here, not by its prompt: <c>allow_read</c>, <c>allow_send</c> and <c>allow_update</c>
/// gate the tools it is given at all, and <c>allowed_recipients</c> limits who it may write to. Each is enforced twice:
/// a tool that is not allowed is never advertised, and is refused if it is called anyway. Nothing here deletes an e-mail.
/// </para>
/// </summary>
public sealed class AIAgentEmailToolNode : ResourceNode, IAgentTool
{
    private const string DefaultPrefix = "email";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IEmailInbox _inbox;
    private readonly IEmailSender _sender;

    public AIAgentEmailToolNode(CredentialsService credentialsService, ILogger<AIAgentEmailToolNode> logger)
        : this(new EmailService(credentialsService ?? throw new ArgumentNullException(nameof(credentialsService)), logger ?? throw new ArgumentNullException(nameof(logger)))) { }

    private AIAgentEmailToolNode(EmailService service)
        : this(service, service) { }

    internal AIAgentEmailToolNode(IEmailInbox inbox, IEmailSender sender)
        : base("ai.tools.communication.email")
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata(
            "AI Agent Email Tool",
            "Gives an agent a mailbox: search and read e-mails (IMAP), answer or write e-mails (SMTP), and mark or file what it handled. What it may do, and who it may write to, is set on the node.",
            "ai.tools.communication",
            whenToUse: "For an agent that works from a mailbox: a support desk answering requests, an assistant sorting what arrived, an agent that sends its result by e-mail. Give only the permissions the job needs."
        );
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal | NodeEffects.SendsMessage);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Email Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineCredentialsInput("inbox", "imap", "The mailbox the agent reads (IMAP connection). Needed for reading, answering and filing.", ui: new ArgumentUiMetadata { Group = "Connections", Order = 0 }, required: false);
        DefineCredentialsInput("outbox", "smtp", "The connection the agent sends with (SMTP). Needed for answering and writing.", ui: new ArgumentUiMetadata { Group = "Connections", Order = 1 }, required: false);
        DefineInput("folder", ArgumentType.Text, "The folder the agent works in", "INBOX", ui: new ArgumentUiMetadata { Group = "Connections", Order = 2 }, required: false);
        DefineInput(
            "tool_name_prefix",
            ArgumentType.Text,
            "Prefix for this instance's tool names, e.g. \"support\" → support_search, support_read, support_reply, support_send, support_update. Required when an agent has more than one Email tool connected: tool names must be unique per agent.",
            DefaultPrefix,
            ui: new ArgumentUiMetadata { Group = "Tools", Order = 10 },
            required: false
        );
        DefineInput("allow_read", ArgumentType.Boolean, "Let the agent search and read e-mails.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 20 }, required: false);
        DefineInput("allow_send", ArgumentType.Boolean, "Let the agent send e-mails (answers and new ones).", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 21 }, required: false);
        DefineInput("allow_update", ArgumentType.Boolean, "Let the agent mark e-mails as read, flag them or move them to a folder.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 22 }, required: false);
        DefineInput(
            "allowed_recipients",
            ArgumentType.Text,
            "Who the agent may write to: addresses or domains separated by commas (anna@shop.com, @shop.com). Empty = anyone. An answer to an e-mail it read is always allowed.",
            "",
            ui: new ArgumentUiMetadata { Group = "Permissions", Order = 23 },
            required: false
        );
        DefineInput("max_body_chars", ArgumentType.Number, "The most characters of an e-mail's text handed to the agent", 12000, ui: new ArgumentUiMetadata { Group = "Tools", Order = 12 }, required: false);

        DefineIcon(NodeIcon.Material(0xe0be, "#5b55e8"));
    }

    private static string Prefix(ResourceBinding binding) => binding.GetString("tool_name_prefix")?.Trim() is { Length: > 0 } prefix ? prefix : DefaultPrefix;

    private static bool Allowed(ResourceBinding binding, string key) => binding.GetBool(key) ?? true;

    private static string? Connection(ResourceBinding binding, string key) => binding.GetString(key)?.Trim() is { Length: > 0 } id ? id : null;

    /// <summary>What this instance can really do: a permission only counts when the connection it needs is there.</summary>
    private static (bool Read, bool Send, bool Reply, bool Update) Abilities(ResourceBinding binding)
    {
        bool hasInbox = Connection(binding, "inbox") is not null;
        bool hasOutbox = Connection(binding, "outbox") is not null;
        bool send = hasOutbox && Allowed(binding, "allow_send");
        return (Read: hasInbox && Allowed(binding, "allow_read"), Send: send, Reply: send && hasInbox, Update: hasInbox && Allowed(binding, "allow_update"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string prefix = Prefix(binding);
        var can = Abilities(binding);
        var tools = new List<JsonObject>();

        if (can.Read)
        {
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_search",
                "List the e-mails of the mailbox, newest first, optionally narrowed. Returns for each: id, from, subject, date, whether it was read, a short preview and its attachments. Use the id with the other tools.",
                """
                {
                  "type": "object",
                  "properties": {
                    "unread_only": { "type": "boolean", "description": "Only the e-mails not read yet (default true)." },
                    "from": { "type": "string", "description": "Only from an address or a name containing this." },
                    "subject_contains": { "type": "string", "description": "Only with this in the subject." },
                    "text_contains": { "type": "string", "description": "Only with this in the text." },
                    "since": { "type": "string", "description": "Only received on or after this date, as 2026-01-31." },
                    "limit": { "type": "integer", "description": "How many at most (default 10, up to 50)." }
                  },
                  "additionalProperties": false
                }
                """
            ));
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_read",
                "Read one e-mail in full by its id: who sent it, when, its subject, its whole text and the names of its attachments.",
                """
                {
                  "type": "object",
                  "properties": { "id": { "type": "string", "description": "The id given by the search." } },
                  "required": ["id"],
                  "additionalProperties": false
                }
                """
            ));
        }

        if (can.Reply)
        {
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_reply",
                "Answer an e-mail of the mailbox. It goes to whoever sent it, with its subject, in the same conversation: give only the id and the text of the answer.",
                """
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "description": "The id of the e-mail to answer." },
                    "body": { "type": "string", "description": "The text of the answer." },
                    "is_html": { "type": "boolean", "description": "Whether the text is HTML (default false)." },
                    "mark_as_read": { "type": "boolean", "description": "Mark the answered e-mail as read (default true)." }
                  },
                  "required": ["id", "body"],
                  "additionalProperties": false
                }
                """,
                requiresHumanReview: false
            ));
        }

        if (can.Send)
        {
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_send",
                "Send a new e-mail. To answer an e-mail of the mailbox, use the reply tool instead.",
                """
                {
                  "type": "object",
                  "properties": {
                    "to": { "type": "string", "description": "Recipient address(es), separated by commas." },
                    "cc": { "type": "string", "description": "Addresses that receive a copy." },
                    "subject": { "type": "string" },
                    "body": { "type": "string" },
                    "is_html": { "type": "boolean", "description": "Whether the body is HTML (default false)." }
                  },
                  "required": ["to", "subject", "body"],
                  "additionalProperties": false
                }
                """
            ));
        }

        if (can.Update)
        {
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_update",
                "Mark e-mails as read or unread, flag or unflag them, or move them to another folder of the mailbox. Nothing is ever deleted.",
                """
                {
                  "type": "object",
                  "properties": {
                    "ids": { "type": "array", "items": { "type": "string" }, "description": "The ids given by the search." },
                    "action": { "type": "string", "enum": ["mark_read", "mark_unread", "flag", "unflag", "move"] },
                    "target_folder": { "type": "string", "description": "For move: the folder to move them to. It must exist." }
                  },
                  "required": ["ids", "action"],
                  "additionalProperties": false
                }
                """
            ));
        }

        return ValueTask.FromResult<IReadOnlyList<JsonObject>>(tools);
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        string prefix = Prefix(binding);
        string operation = toolName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) ? toolName[(prefix.Length + 1)..].ToLowerInvariant() : string.Empty;
        var can = Abilities(binding);
        string? folder = binding.GetString("folder");
        int maxBody = Math.Max(binding.GetInt("max_body_chars") ?? 12000, 200);

        try
        {
            switch (operation)
            {
                case "search" when can.Read:
                {
                    var query = new EmailQuery(
                        UnreadOnly: Bool(arguments, "unread_only") ?? true,
                        From: Text(arguments, "from"),
                        SubjectContains: Text(arguments, "subject_contains"),
                        TextContains: Text(arguments, "text_contains"),
                        Since: Date(arguments, "since"),
                        Limit: Math.Clamp(Int(arguments, "limit") ?? 10, 1, 50)
                    );
                    IReadOnlyList<EmailMessage> found = await _inbox.SearchAsync(Connection(binding, "inbox")!, folder, query, maxBodyChars: 0, markAsRead: false, cancellationToken).ConfigureAwait(false);
                    return Result(new { count = found.Count, emails = found.Select(Listed) });
                }

                case "read" when can.Read:
                {
                    string id = Text(arguments, "id") ?? throw new InvalidOperationException("'id' is required.");
                    EmailMessage? email = await _inbox.GetAsync(Connection(binding, "inbox")!, folder, id, maxBody, markAsRead: false, cancellationToken).ConfigureAwait(false);
                    return email is null ? Error($"There is no e-mail '{id}' in this folder. Use an id given by {prefix}_search.") : Result(EmailMapping.ToJson(email));
                }

                case "reply" when can.Reply:
                {
                    string id = Text(arguments, "id") ?? throw new InvalidOperationException("'id' is required.");
                    string body = Text(arguments, "body") ?? throw new InvalidOperationException("'body' is required.");
                    EmailMessage? original = await _inbox.GetAsync(Connection(binding, "inbox")!, folder, id, maxBodyChars: 1, markAsRead: false, cancellationToken).ConfigureAwait(false);
                    if (original is null)
                    {
                        return Error($"There is no e-mail '{id}' in this folder. Use an id given by {prefix}_search.");
                    }

                    // An answer goes to whoever wrote: that is allowed whatever the list of recipients says.
                    OutgoingEmail reply = EmailMapping.ReplyTo(original, body, Bool(arguments, "is_html") ?? false);
                    string messageId = await _sender.SendAsync(Connection(binding, "outbox")!, reply, cancellationToken).ConfigureAwait(false);
                    if ((Bool(arguments, "mark_as_read") ?? true) && can.Update)
                    {
                        _ = await _inbox.UpdateAsync(Connection(binding, "inbox")!, folder, [id], EmailUpdate.MarkRead, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    return Result(new { sent = true, to = reply.To, subject = reply.Subject, message_id = messageId });
                }

                case "send" when can.Send:
                {
                    string to = Text(arguments, "to") ?? throw new InvalidOperationException("'to' is required.");
                    string? cc = Text(arguments, "cc");
                    if (NotAllowed(binding, to, cc) is { } refused)
                    {
                        return Error(refused);
                    }

                    var email = new OutgoingEmail(
                        To: to,
                        Subject: Text(arguments, "subject") ?? throw new InvalidOperationException("'subject' is required."),
                        Body: Text(arguments, "body") ?? string.Empty,
                        IsHtml: Bool(arguments, "is_html") ?? false,
                        Cc: cc
                    );
                    string messageId = await _sender.SendAsync(Connection(binding, "outbox")!, email, cancellationToken).ConfigureAwait(false);
                    return Result(new { sent = true, to, subject = email.Subject, message_id = messageId });
                }

                case "update" when can.Update:
                {
                    List<string> ids = arguments.TryGetProperty("ids", out JsonElement list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Select(i => i.ToString()).Where(i => i.Length > 0)] : [];
                    int updated = await _inbox
                        .UpdateAsync(Connection(binding, "inbox")!, folder, ids, EmailMapping.ParseUpdate(Text(arguments, "action")), Text(arguments, "target_folder"), cancellationToken)
                        .ConfigureAwait(false);
                    return Result(new { updated });
                }

                case "search" or "read" or "reply" or "send" or "update":
                    return Error($"'{toolName}' is not allowed on this mailbox tool (it is switched off, or the connection it needs is not set).");

                default:
                    return Error($"Tool '{toolName}' is not exposed by this node.");
            }
        }
        catch (InvalidOperationException ex)
        {
            // What the mailbox or the agent got wrong is said to the agent, which can correct itself or tell the person.
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Why the agent may not write to these addresses, or null when it may. The list holds addresses (anna@shop.com) and
    /// domains (@shop.com or shop.com); every recipient must match one of them.
    /// </summary>
    internal static string? NotAllowed(ResourceBinding binding, string to, string? cc)
    {
        List<string> allowed = [.. EmailMapping.SplitAddresses(binding.GetString("allowed_recipients")).Select(a => a.ToLowerInvariant())];
        if (allowed.Count == 0)
        {
            return null;
        }

        foreach (string recipient in EmailMapping.SplitAddresses(to).Concat(EmailMapping.SplitAddresses(cc)))
        {
            // "Anna <anna@shop.com>" → anna@shop.com
            string address = (recipient.Contains('<') && recipient.Contains('>') ? recipient[(recipient.IndexOf('<') + 1)..recipient.LastIndexOf('>')] : recipient).Trim().ToLowerInvariant();
            string domain = address.Contains('@') ? address[address.LastIndexOf('@')..] : string.Empty;
            bool ok = allowed.Any(a => a == address || (a.StartsWith('@') ? a == domain : !a.Contains('@') && "@" + a == domain));
            if (!ok)
            {
                return $"This mailbox tool may not write to '{address}'. It may only write to: {string.Join(", ", allowed)}. Answering an e-mail that was received is always allowed (use the reply tool).";
            }
        }

        return null;
    }

    private static object Listed(EmailMessage email) =>
        new
        {
            id = email.Id,
            from = email.From,
            subject = email.Subject,
            date = email.Date == default ? null : email.Date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            is_read = email.IsRead,
            preview = email.Preview,
            attachments = email.Attachments.Select(a => a.Name),
        };

    private static JsonObject Result(object payload) => McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), false);

    private static JsonObject Error(string message) => McpToolResultJson.CreateTextResult(message, true);

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static bool? Bool(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Int(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    private static DateTimeOffset? Date(JsonElement arguments, string name) =>
        Text(arguments, name) is { } text
            ? DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed
                : throw new InvalidOperationException($"'{text}' is not a date. Write it as 2026-01-31.")
            : null;
}
