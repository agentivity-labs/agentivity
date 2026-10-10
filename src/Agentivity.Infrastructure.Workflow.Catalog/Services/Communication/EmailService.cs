using System.Net;
using System.Net.Mail;
using System.Text;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.Communication;

public sealed class EmailService
{
    private readonly CredentialsService _credentialsService;
    private readonly ILogger _logger;

    public EmailService(CredentialsService credentialsService, ILogger logger)
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask SendAsync(string credentialsId, string recipients, string? replyTo, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipients);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        EmailSettings settings = await ResolveSettingsAsync(credentialsId, cancellationToken).ConfigureAwait(false);

        using var message = new MailMessage
        {
            From = string.IsNullOrWhiteSpace(settings.FromName) ? new MailAddress(settings.FromEmail) : new MailAddress(settings.FromEmail, settings.FromName),
            Subject = subject,
            Body = body ?? string.Empty,
            IsBodyHtml = isHtml,
        };

        AddAddresses(message.To, recipients);
        AddReplyToAddresses(message, replyTo);

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
        };

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            client.Credentials = new NetworkCredential(settings.Username, settings.Password ?? string.Empty);
        }

        _logger.LogInformation("Sending email to {Recipients} with subject '{Subject}'", recipients, subject);
        await client.SendMailAsync(message).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<EmailSettings> ResolveSettingsAsync(string credentialsId, CancellationToken cancellationToken)
    {
        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string? host = credentials.GetField("host")?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("SMTP credentials are missing the 'host' field.");
        }

        string? fromEmail = credentials.GetField("from_email")?.Trim();
        return string.IsNullOrWhiteSpace(fromEmail)
            ? throw new InvalidOperationException("SMTP credentials are missing the 'from_email' field.")
            : new EmailSettings(
                Host: host,
                Port: ParsePort(credentials),
                Username: credentials.GetField("username")?.Trim(),
                Password: credentials.GetField("password"),
                FromEmail: fromEmail,
                FromName: credentials.GetField("from_name")?.Trim(),
                EnableSsl: ParseBool(credentials, "enable_ssl", defaultValue: true)
            );
    }

    private static int ParsePort(Credentials credentials)
    {
        string? rawValue = credentials.GetField("port");
        return int.TryParse(rawValue, out int port) && port > 0 ? port : 587;
    }

    private static bool ParseBool(Credentials credentials, string fieldName, bool defaultValue)
    {
        string? rawValue = credentials.GetField(fieldName);
        return bool.TryParse(rawValue, out bool result) ? result : defaultValue;
    }

    private static void AddAddresses(MailAddressCollection collection, string addresses)
    {
        foreach (string address in SplitAddresses(addresses))
        {
            collection.Add(address);
        }

        if (collection.Count == 0)
        {
            throw new InvalidOperationException("At least one email recipient is required.");
        }
    }

    private static void AddReplyToAddresses(MailMessage message, string? replyTo)
    {
        if (string.IsNullOrWhiteSpace(replyTo))
        {
            return;
        }

        foreach (string address in SplitAddresses(replyTo))
        {
            message.ReplyToList.Add(new MailAddress(address));
        }
    }

    private static IReadOnlyList<string> SplitAddresses(string addresses) => addresses.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(address => !string.IsNullOrWhiteSpace(address)).ToArray();

    private sealed record EmailSettings(string Host, int Port, string? Username, string? Password, string FromEmail, string? FromName, bool EnableSsl);
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
