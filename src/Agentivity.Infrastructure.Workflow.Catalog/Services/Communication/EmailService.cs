using System.Net.Sockets;
using System.Text;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;

/// <summary>An e-mail to send. Addresses are written as people write them: separated by commas, semicolons or line breaks.</summary>
/// <param name="InReplyTo">The Message-ID of the e-mail this one answers: mail programs then show both in one conversation.</param>
/// <param name="References">The Message-IDs of the conversation so far, oldest first (the answered e-mail's own references, then its id).</param>
public sealed record OutgoingEmail(string To, string Subject, string Body, bool IsHtml = false, string? Cc = null, string? Bcc = null, string? ReplyTo = null, string? InReplyTo = null, IReadOnlyList<string>? References = null);

/// <summary>What narrows a reading of a mailbox. Everything left out does not filter.</summary>
public sealed record EmailQuery(bool UnreadOnly = false, string? From = null, string? SubjectContains = null, string? TextContains = null, DateTimeOffset? Since = null, int Limit = 20);

/// <summary>A file attached to an e-mail: what it is, never its content.</summary>
public sealed record EmailAttachmentInfo(string Name, string ContentType, long? Size);

/// <summary>
/// An e-mail as read from a mailbox. <see cref="Id"/> identifies it inside <see cref="Folder"/> (it is what the other
/// operations take); <see cref="MessageId"/> is the id the e-mail carries everywhere, used to answer it. <see cref="Body"/> is
/// the text of the e-mail (an HTML-only e-mail is turned into text); it is null in a listing that did not ask for it.
/// </summary>
public sealed record EmailMessage(
    string Id,
    string Folder,
    string? MessageId,
    string From,
    string To,
    string? Cc,
    string? ReplyTo,
    string Subject,
    DateTimeOffset Date,
    bool IsRead,
    bool IsFlagged,
    string? Preview,
    string? Body,
    IReadOnlyList<EmailAttachmentInfo> Attachments,
    IReadOnlyList<string> References
);

/// <summary>What can be done to e-mails that are already in a mailbox. Nothing here deletes.</summary>
public enum EmailUpdate
{
    MarkRead,
    MarkUnread,
    Flag,
    Unflag,
    Move,
}

/// <summary>Sends e-mails through an SMTP connection of the workspace.</summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="email"/> and returns the Message-ID it was given.</summary>
    ValueTask<string> SendAsync(string credentialsId, OutgoingEmail email, CancellationToken cancellationToken = default);
}

/// <summary>Reads and files the e-mails of a mailbox through an IMAP connection of the workspace.</summary>
public interface IEmailInbox
{
    /// <summary>The e-mails of <paramref name="folder"/> that match, newest first. <paramref name="maxBodyChars"/> 0 leaves the bodies out.</summary>
    ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(string credentialsId, string? folder, EmailQuery query, int maxBodyChars = 0, bool markAsRead = false, CancellationToken cancellationToken = default);

    /// <summary>One e-mail in full, or null when <paramref name="id"/> is not in the folder.</summary>
    ValueTask<EmailMessage?> GetAsync(string credentialsId, string? folder, string id, int maxBodyChars = 0, bool markAsRead = false, CancellationToken cancellationToken = default);

    /// <summary>Applies <paramref name="update"/> to the e-mails named and returns how many were found.</summary>
    ValueTask<int> UpdateAsync(string credentialsId, string? folder, IReadOnlyList<string> ids, EmailUpdate update, string? targetFolder = null, CancellationToken cancellationToken = default);

    /// <summary>The folders of the mailbox, by their full name.</summary>
    ValueTask<IReadOnlyList<string>> ListFoldersAsync(string credentialsId, CancellationToken cancellationToken = default);
}

/// <summary>
/// E-mail over the standard protocols: SMTP to send, IMAP to read. One connection per operation, opened and closed here; a
/// failure to reach the server or to sign in is reported in words that say which server and what to check.
/// </summary>
public sealed class EmailService : IEmailSender, IEmailInbox
{
    private const string Inbox = "INBOX";

    private readonly CredentialsService _credentialsService;
    private readonly ILogger _logger;

    public EmailService(CredentialsService credentialsService, ILogger logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Sending ───────────────────────────────────────────────────────────────────────────────────

    public async ValueTask SendAsync(string credentialsId, string recipients, string? replyTo, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default) =>
        _ = await SendAsync(credentialsId, new OutgoingEmail(recipients, subject, body, isHtml, ReplyTo: replyTo), cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> SendAsync(string credentialsId, OutgoingEmail email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email.To, "recipients");
        ArgumentException.ThrowIfNullOrWhiteSpace(email.Subject, "subject");

        Credentials credentials = await RequireCredentialsAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        string host = RequireField(credentials, "host", "SMTP");
        string fromEmail = RequireField(credentials, "from_email", "SMTP");
        int port = Port(credentials, 587);

        MimeMessage message = EmailMapping.Compose(email, fromEmail, credentials.GetField("from_name")?.Trim());

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, Security(credentials, port, implicitTlsPort: 465), cancellationToken).ConfigureAwait(false);
            if (credentials.GetField("username")?.Trim() is { Length: > 0 } username)
            {
                await client.AuthenticateAsync(username, credentials.GetField("password") ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Sending email to {Recipients} with subject '{Subject}'", email.To, email.Subject);
            _ = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Explain(ex, "SMTP", host, port) is { } explained)
        {
            throw explained;
        }

        return message.MessageId;
    }

    // ── Reading ───────────────────────────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(string credentialsId, string? folder, EmailQuery query, int maxBodyChars = 0, bool markAsRead = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await WithFolderAsync(
            credentialsId,
            folder,
            markAsRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly,
            async (_, mailbox, name) =>
            {
                IList<UniqueId> found = await mailbox.SearchAsync(EmailMapping.ToSearch(query), cancellationToken).ConfigureAwait(false);
                // Newest first: in a folder, a later e-mail has a higher id.
                List<UniqueId> wanted = [.. found.OrderByDescending(u => u.Id).Take(Math.Clamp(query.Limit, 1, 200))];
                if (wanted.Count == 0)
                {
                    return (IReadOnlyList<EmailMessage>)[];
                }

                var result = new List<EmailMessage>(wanted.Count);
                if (maxBodyChars > 0)
                {
                    foreach (UniqueId uid in wanted)
                    {
                        MimeMessage full = await mailbox.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                        IList<IMessageSummary> flags = await mailbox.FetchAsync([uid], MessageSummaryItems.Flags | MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
                        result.Add(EmailMapping.FromMessage(full, uid.Id.ToString(), name, flags.FirstOrDefault()?.Flags ?? MessageFlags.None, maxBodyChars));
                    }
                }
                else
                {
                    IList<IMessageSummary> summaries = await mailbox
                        .FetchAsync(wanted, MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText | MessageSummaryItems.References, cancellationToken)
                        .ConfigureAwait(false);
                    result.AddRange(summaries.OrderByDescending(s => s.UniqueId.Id).Select(s => EmailMapping.FromSummary(s, name)));
                }

                if (markAsRead)
                {
                    await mailbox.AddFlagsAsync(wanted, MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                }

                return (IReadOnlyList<EmailMessage>)result;
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async ValueTask<EmailMessage?> GetAsync(string credentialsId, string? folder, string id, int maxBodyChars = 0, bool markAsRead = false, CancellationToken cancellationToken = default)
    {
        UniqueId uid = ParseId(id);
        return await WithFolderAsync(
            credentialsId,
            folder,
            markAsRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly,
            async (_, mailbox, name) =>
            {
                IList<IMessageSummary> flags = await mailbox.FetchAsync([uid], MessageSummaryItems.Flags | MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
                if (flags.Count == 0)
                {
                    return null;
                }

                MimeMessage message = await mailbox.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                if (markAsRead)
                {
                    await mailbox.AddFlagsAsync([uid], MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                }

                return EmailMapping.FromMessage(message, uid.Id.ToString(), name, flags[0].Flags ?? MessageFlags.None, maxBodyChars <= 0 ? int.MaxValue : maxBodyChars);
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async ValueTask<int> UpdateAsync(string credentialsId, string? folder, IReadOnlyList<string> ids, EmailUpdate update, string? targetFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        List<UniqueId> uids = [.. ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(ParseId)];
        if (uids.Count == 0)
        {
            throw new InvalidOperationException("At least one e-mail id is required.");
        }

        if (update == EmailUpdate.Move && string.IsNullOrWhiteSpace(targetFolder))
        {
            throw new InvalidOperationException("Moving e-mails needs the folder to move them to.");
        }

        return await WithFolderAsync(
            credentialsId,
            folder,
            FolderAccess.ReadWrite,
            async (client, mailbox, folderName) =>
            {
                // Only what is really there is counted, and touched.
                IList<IMessageSummary> present = await mailbox.FetchAsync(uids, MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
                List<UniqueId> existing = [.. present.Select(p => p.UniqueId)];
                if (existing.Count == 0)
                {
                    return 0;
                }

                switch (update)
                {
                    case EmailUpdate.MarkRead:
                        await mailbox.AddFlagsAsync(existing, MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                        break;
                    case EmailUpdate.MarkUnread:
                        await mailbox.RemoveFlagsAsync(existing, MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                        break;
                    case EmailUpdate.Flag:
                        await mailbox.AddFlagsAsync(existing, MessageFlags.Flagged, true, cancellationToken).ConfigureAwait(false);
                        break;
                    case EmailUpdate.Unflag:
                        await mailbox.RemoveFlagsAsync(existing, MessageFlags.Flagged, true, cancellationToken).ConfigureAwait(false);
                        break;
                    case EmailUpdate.Move:
                        IMailFolder destination = await TargetAsync(client, targetFolder!, cancellationToken).ConfigureAwait(false);
                        _ = await mailbox.MoveToAsync(existing, destination, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException($"'{update}' is not something that can be done to an e-mail.");
                }

                return existing.Count;
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> ListFoldersAsync(string credentialsId, CancellationToken cancellationToken = default) =>
        await WithClientAsync(
            credentialsId,
            async client =>
            {
                var names = new List<string> { Inbox };
                foreach (FolderNamespace ns in client.PersonalNamespaces)
                {
                    foreach (IMailFolder f in await client.GetFoldersAsync(ns, cancellationToken: cancellationToken).ConfigureAwait(false))
                    {
                        if (!names.Contains(f.FullName, StringComparer.OrdinalIgnoreCase))
                        {
                            names.Add(f.FullName);
                        }
                    }
                }

                return (IReadOnlyList<string>)names;
            },
            cancellationToken
        ).ConfigureAwait(false);

    private static async Task<IMailFolder> TargetAsync(ImapClient client, string target, CancellationToken cancellationToken)
    {
        // The destination is named as the mailbox names it; it is not opened, a move only needs to know it.
        try
        {
            return string.Equals(target.Trim(), Inbox, StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(target.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (FolderNotFoundException)
        {
            throw new InvalidOperationException($"The mailbox has no folder named '{target}'.");
        }
    }

    private async ValueTask<T> WithFolderAsync<T>(string credentialsId, string? folder, FolderAccess access, Func<ImapClient, IMailFolder, string, Task<T>> work, CancellationToken cancellationToken) =>
        await WithClientAsync(
            credentialsId,
            async client =>
            {
                string name = string.IsNullOrWhiteSpace(folder) ? Inbox : folder.Trim();
                IMailFolder mailbox;
                try
                {
                    mailbox = string.Equals(name, Inbox, StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(name, cancellationToken).ConfigureAwait(false);
                }
                catch (FolderNotFoundException)
                {
                    throw new InvalidOperationException($"The mailbox has no folder named '{name}'.");
                }

                _ = await mailbox.OpenAsync(access, cancellationToken).ConfigureAwait(false);
                T result = await work(client, mailbox, mailbox.FullName).ConfigureAwait(false);
                await mailbox.CloseAsync(false, cancellationToken).ConfigureAwait(false);
                return result;
            },
            cancellationToken
        ).ConfigureAwait(false);

    private async ValueTask<T> WithClientAsync<T>(string credentialsId, Func<ImapClient, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        Credentials credentials = await RequireCredentialsAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        string host = RequireField(credentials, "host", "IMAP");
        string username = RequireField(credentials, "username", "IMAP");
        int port = Port(credentials, 993);

        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(host, port, Security(credentials, port, implicitTlsPort: 993), cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(username, credentials.GetField("password") ?? string.Empty, cancellationToken).ConfigureAwait(false);
            T result = await work(client).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (Explain(ex, "IMAP", host, port) is { } explained)
        {
            throw explained;
        }
    }

    // ── Connections ───────────────────────────────────────────────────────────────────────────────

    private async Task<Credentials> RequireCredentialsAsync(string credentialsId, CancellationToken cancellationToken) =>
        await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

    private static string RequireField(Credentials credentials, string field, string kind) =>
        credentials.GetField(field)?.Trim() is { Length: > 0 } value ? value : throw new InvalidOperationException($"{kind} credentials are missing the '{field}' field.");

    private static int Port(Credentials credentials, int fallback) => int.TryParse(credentials.GetField("port"), out int port) && port > 0 ? port : fallback;

    /// <summary>
    /// How the connection is protected. <c>security</c> says it outright (<c>ssl</c>, <c>starttls</c>, <c>none</c>); left on
    /// <c>auto</c> (or absent), the port decides: the port reserved for it means encrypted from the first byte, any other one
    /// starts in clear and must switch to encryption before anything is said (so a password is never sent in clear). The older
    /// <c>enable_ssl: false</c> still means no encryption at all.
    /// </summary>
    internal static SecureSocketOptions Security(Credentials credentials, int port, int implicitTlsPort)
    {
        switch (credentials.GetField("security")?.Trim().ToLowerInvariant())
        {
            case "ssl" or "tls":
                return SecureSocketOptions.SslOnConnect;
            case "starttls":
                return SecureSocketOptions.StartTls;
            case "none":
                return SecureSocketOptions.None;
        }

        if (bool.TryParse(credentials.GetField("enable_ssl"), out bool enabled) && !enabled)
        {
            return SecureSocketOptions.None;
        }

        return port == implicitTlsPort ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
    }

    /// <summary>A failure of the connection itself, said so that the person knows what to look at. Null for anything else.</summary>
    private static InvalidOperationException? Explain(Exception ex, string kind, string host, int port) =>
        ex switch
        {
            AuthenticationException => new InvalidOperationException($"The {kind} server {host} refused the user name or the password. Check them in the connection (some providers want an app password).", ex),
            SslHandshakeException => new InvalidOperationException($"No secure connection could be made with the {kind} server {host}:{port}. Check the port and the security of the connection (port {(kind == "SMTP" ? "465" : "993")} is encrypted from the start, {(kind == "SMTP" ? "587" : "143")} uses STARTTLS).", ex),
            SocketException or IOException or TimeoutException => new InvalidOperationException($"The {kind} server {host}:{port} could not be reached: {ex.Message}", ex),
            ServiceNotConnectedException or ServiceNotAuthenticatedException or ProtocolException => new InvalidOperationException($"The {kind} server {host}:{port} did not accept the conversation: {ex.Message}", ex),
            SmtpCommandException smtp => new InvalidOperationException($"The SMTP server {host} refused the e-mail: {smtp.Message}", ex),
            ImapCommandException imap => new InvalidOperationException($"The IMAP server {host} refused the request: {imap.Message}", ex),
            _ => null,
        };

    private static UniqueId ParseId(string id) =>
        uint.TryParse(id?.Trim(), out uint value) && value > 0 ? new UniqueId(value) : throw new InvalidOperationException($"'{id}' is not an e-mail id. Use the id given when the e-mails were listed.");
}

/// <summary>Between what the platform says of an e-mail and what the mail libraries say: pure, so it is tested without a server.</summary>
public static class EmailMapping
{
    private static readonly char[] AddressSeparators = [';', ',', '\r', '\n'];

    /// <summary>The addresses written in <paramref name="addresses"/>, one per entry.</summary>
    public static IReadOnlyList<string> SplitAddresses(string? addresses) =>
        string.IsNullOrWhiteSpace(addresses) ? [] : [.. addresses.Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(a => !string.IsNullOrWhiteSpace(a))];

    public static MimeMessage Compose(OutgoingEmail email, string fromEmail, string? fromName)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(fromName) ? null : fromName, fromEmail));

        AddAddresses(message.To, email.To, "recipient");
        if (message.To.Count == 0)
        {
            throw new InvalidOperationException("At least one email recipient is required.");
        }

        AddAddresses(message.Cc, email.Cc, "copy (cc) recipient");
        AddAddresses(message.Bcc, email.Bcc, "hidden copy (bcc) recipient");
        AddAddresses(message.ReplyTo, email.ReplyTo, "reply-to address");

        message.Subject = email.Subject;
        message.Body = new TextPart(email.IsHtml ? "html" : "plain") { Text = email.Body ?? string.Empty };

        if (NormalizeMessageId(email.InReplyTo) is { } answered)
        {
            message.InReplyTo = answered;
            foreach (string reference in (email.References ?? []).Select(NormalizeMessageId).OfType<string>())
            {
                if (!message.References.Contains(reference))
                {
                    message.References.Add(reference);
                }
            }

            if (!message.References.Contains(answered))
            {
                message.References.Add(answered);
            }
        }

        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(fromEmail.Contains('@') ? fromEmail[(fromEmail.LastIndexOf('@') + 1)..] : "localhost");
        return message;
    }

    private static void AddAddresses(InternetAddressList list, string? addresses, string what)
    {
        foreach (string address in SplitAddresses(addresses))
        {
            if (!MailboxAddress.TryParse(address, out MailboxAddress? parsed) || !parsed.Address.Contains('@'))
            {
                throw new InvalidOperationException($"'{address}' is not an e-mail address ({what}).");
            }

            list.Add(parsed);
        }
    }

    /// <summary>A Message-ID as the headers carry it: without the angle brackets.</summary>
    public static string? NormalizeMessageId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().TrimStart('<').TrimEnd('>');

    public static SearchQuery ToSearch(EmailQuery query)
    {
        SearchQuery search = query.UnreadOnly ? SearchQuery.NotSeen : SearchQuery.All;
        if (!string.IsNullOrWhiteSpace(query.From))
        {
            search = search.And(SearchQuery.FromContains(query.From.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.SubjectContains))
        {
            search = search.And(SearchQuery.SubjectContains(query.SubjectContains.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.TextContains))
        {
            search = search.And(SearchQuery.BodyContains(query.TextContains.Trim()));
        }

        if (query.Since is { } since)
        {
            // IMAP compares days, not instants.
            search = search.And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)));
        }

        return search;
    }

    /// <summary>An e-mail from its envelope alone (a listing): no body, a short preview when the server gives one.</summary>
    public static EmailMessage FromSummary(IMessageSummary summary, string folder)
    {
        Envelope? envelope = summary.Envelope;
        MessageFlags flags = summary.Flags ?? MessageFlags.None;
        return new EmailMessage(
            Id: summary.UniqueId.Id.ToString(),
            Folder: folder,
            MessageId: NormalizeMessageId(envelope?.MessageId),
            From: envelope?.From.ToString() ?? string.Empty,
            To: envelope?.To.ToString() ?? string.Empty,
            Cc: envelope is { Cc.Count: > 0 } ? envelope.Cc.ToString() : null,
            ReplyTo: envelope is { ReplyTo.Count: > 0 } ? envelope.ReplyTo.ToString() : null,
            Subject: envelope?.Subject ?? string.Empty,
            Date: envelope?.Date ?? summary.InternalDate ?? default,
            IsRead: flags.HasFlag(MessageFlags.Seen),
            IsFlagged: flags.HasFlag(MessageFlags.Flagged),
            Preview: string.IsNullOrWhiteSpace(summary.PreviewText) ? null : Tidy(summary.PreviewText),
            Body: null,
            Attachments: [.. (summary.Attachments ?? []).Select(a => new EmailAttachmentInfo(a.FileName ?? a.ContentType?.Name ?? "attachment", a.ContentType?.MimeType ?? "application/octet-stream", a.Octets))],
            References: [.. (summary.References ?? []).Select(NormalizeMessageId).OfType<string>()]
        );
    }

    /// <summary>An e-mail in full. Its text is the plain-text part, or the HTML one turned into text; cut at <paramref name="maxBodyChars"/>.</summary>
    public static EmailMessage FromMessage(MimeMessage message, string id, string folder, MessageFlags flags, int maxBodyChars)
    {
        string body = message.TextBody is { Length: > 0 } text ? text : HtmlToText(message.HtmlBody);
        body = body.Trim();
        string preview = Tidy(body);
        return new EmailMessage(
            Id: id,
            Folder: folder,
            MessageId: NormalizeMessageId(message.MessageId),
            From: message.From.ToString(),
            To: message.To.ToString(),
            Cc: message.Cc.Count > 0 ? message.Cc.ToString() : null,
            ReplyTo: message.ReplyTo.Count > 0 ? message.ReplyTo.ToString() : null,
            Subject: message.Subject ?? string.Empty,
            Date: message.Date,
            IsRead: flags.HasFlag(MessageFlags.Seen),
            IsFlagged: flags.HasFlag(MessageFlags.Flagged),
            Preview: preview.Length == 0 ? null : (preview.Length > 200 ? preview[..200] + "…" : preview),
            Body: body.Length > maxBodyChars ? body[..maxBodyChars] + $"\n[… {body.Length - maxBodyChars} more characters]" : body,
            Attachments:
            [
                .. message.Attachments.Select(a => new EmailAttachmentInfo(
                    (a as MimePart)?.FileName ?? a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "attachment",
                    a.ContentType.MimeType,
                    a is MimePart { Content.Stream: { CanSeek: true } stream } ? stream.Length : null
                )),
            ],
            References: [.. message.References.Select(NormalizeMessageId).OfType<string>()]
        );
    }

    /// <summary>The words of an HTML e-mail, without its markup, its styles or its scripts.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(html);
        foreach (HtmlAgilityPack.HtmlNode node in document.DocumentNode.SelectNodes("//script|//style|//head") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
        {
            node.Remove();
        }

        foreach (HtmlAgilityPack.HtmlNode node in document.DocumentNode.SelectNodes("//br|//p|//div|//tr|//li|//h1|//h2|//h3|//h4") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
        {
            _ = node.ParentNode.InsertBefore(document.CreateTextNode("\n"), node);
        }

        string text = HtmlAgilityPack.HtmlEntity.DeEntitize(document.DocumentNode.InnerText);
        IEnumerable<string> lines = text.Split('\n').Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        var builder = new StringBuilder();
        bool blank = true;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                if (!blank)
                {
                    _ = builder.Append('\n');
                }

                blank = true;
                continue;
            }

            _ = builder.Append(line).Append('\n');
            blank = false;
        }

        return builder.ToString().Trim();
    }

    private static string Tidy(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>How an answer to <paramref name="original"/> is addressed and titled, the way mail programs do it.</summary>
    public static OutgoingEmail ReplyTo(EmailMessage original, string body, bool isHtml = false) =>
        new(
            To: string.IsNullOrWhiteSpace(original.ReplyTo) ? original.From : original.ReplyTo,
            Subject: original.Subject.TrimStart().StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? original.Subject : $"Re: {original.Subject}",
            Body: body,
            IsHtml: isHtml,
            InReplyTo: original.MessageId,
            References: original.References
        );

    /// <summary>An e-mail as the nodes and the agent tools hand it over: plain names, dates in ISO 8601.</summary>
    public static Dictionary<string, object?> ToJson(EmailMessage email) =>
        new()
        {
            ["id"] = email.Id,
            ["folder"] = email.Folder,
            ["message_id"] = email.MessageId,
            ["from"] = email.From,
            ["to"] = email.To,
            ["cc"] = email.Cc,
            ["reply_to"] = email.ReplyTo,
            ["subject"] = email.Subject,
            ["date"] = email.Date == default ? null : email.Date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["is_read"] = email.IsRead,
            ["is_flagged"] = email.IsFlagged,
            ["preview"] = email.Preview,
            ["body"] = email.Body,
            ["attachments"] = email.Attachments.Select(a => new Dictionary<string, object?> { ["name"] = a.Name, ["content_type"] = a.ContentType, ["size"] = a.Size }).ToList(),
        };

    public static EmailUpdate ParseUpdate(string? action) =>
        action?.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "mark_read" or "read" => EmailUpdate.MarkRead,
            "mark_unread" or "unread" => EmailUpdate.MarkUnread,
            "flag" or "star" => EmailUpdate.Flag,
            "unflag" or "unstar" => EmailUpdate.Unflag,
            "move" => EmailUpdate.Move,
            _ => throw new InvalidOperationException($"'{action}' is not something that can be done to an e-mail. Use mark_read, mark_unread, flag, unflag or move."),
        };
}

internal static class EmailFormatting
{
    public static string BuildInteractionSubject(InteractionRequest request) => $"[{request.Kind}] {request.Title}";

    public static string BuildInteractionMessage(InteractionRequest request)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine(request.Title);
        _ = builder.AppendLine(new string('=', request.Title.Length));
        _ = builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            _ = builder.AppendLine(request.Description.Trim());
            _ = builder.AppendLine();
        }

        if (request.Fields.Count > 0)
        {
            _ = builder.AppendLine("Expected fields:");
            foreach (InteractionField field in request.Fields)
            {
                _ = builder.Append("- ").Append(field.Label).Append(" (").Append(field.Type).Append(field.Required ? ", required" : ", optional").AppendLine(")");
            }

            _ = builder.AppendLine();
        }

        _ = builder.AppendLine($"Request ID: {request.Id}");
        _ = builder.AppendLine($"Run ID: {request.RunId}");

        if (request.Timeout is not null)
        {
            _ = builder.AppendLine($"Timeout: {request.Timeout}");
        }

        _ = builder.AppendLine();
        _ = builder.AppendLine("This email delivers the interaction request. The response must still be submitted through the workflow interaction UI or API.");
        return builder.ToString().TrimEnd();
    }
}
