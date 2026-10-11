using Agentivity.Infrastructure.Agentic.Library;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Services;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Assistant.Loop;

/// <summary>The model the assistant talks to, and the name of the connection it came from.</summary>
public sealed record AssistantModel(ILlmClient Client, string ConnectionName);

/// <summary>Raised when the assistant has no model to talk to, with a message a person can act on.</summary>
public sealed class AssistantModelUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IAssistantModelProvider
{
    Task<AssistantModel> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Chooses the model connection. A connection named in the assistant's own settings (Assistant.CredentialsId, not the older
/// AIAssistant one) is used as it is: a person who chose one (a
/// local model needs no key) gets that one. When none is named, it is the first model connection of the list that holds a
/// key. Changing the setting changes the model; nothing else is needed.
/// </summary>
public sealed class SettingsAssistantModelProvider(AppSettingsService settings, LlmConnectionReplacer connections, ILlmClientFactory factory, CredentialsService credentials, ILoggerFactory loggers) : IAssistantModelProvider
{
    /// <summary>The wire format a provider speaks: Claude and Gemini each have their own, everything else is OpenAI's.</summary>
    public static LlmApiFlavor FlavorOf(string? templateId) =>
        (templateId ?? "").ToLowerInvariant() switch
        {
            "anthropic" => LlmApiFlavor.Anthropic,
            "gemini" => LlmApiFlavor.Gemini,
            _ => LlmApiFlavor.OpenAI,
        };

    // The value an unset setting carries.
    private const string Unset = "invalid_credential_id";

    /// <summary>Where a provider's API lives when its credential names no address. Unknown providers use the credential's own.</summary>
    public static string? DefaultBaseUrlOf(string? templateId) =>
        (templateId ?? "").ToLowerInvariant() switch
        {
            "anthropic" => "https://api.anthropic.com",
            "gemini" => "https://generativelanguage.googleapis.com/v1beta",
            "xai" => "https://api.x.ai/v1",
            "deepseek" => "https://api.deepseek.com/v1",
            "openai" => "https://api.openai.com/v1",
            _ => null,
        };

    /// <summary>
    /// A client for any model connection of the platform, whatever its provider: it speaks the provider's own format (Claude,
    /// Gemini, AWS Bedrock) or OpenAI's, which most others (DeepSeek, xAI, Groq, Ollama, LM Studio…) share.
    /// </summary>
    private async Task<ILlmClient> CreateClientAsync(string credentialId, CancellationToken ct)
    {
        var credential = await credentials.GetByIdAsync(credentialId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Model connection '{credentialId}' was not found.");
        var template = credential.TemplateId;
        if (string.Equals(template, "aws_bedrock", StringComparison.OrdinalIgnoreCase))
        {
            var accessKey = credential.GetField("access_key_id") ?? throw new InvalidOperationException("The AWS Bedrock connection has no 'access_key_id'.");
            var secret = credential.GetField("secret_access_key") ?? throw new InvalidOperationException("The AWS Bedrock connection has no 'secret_access_key'.");
            var region = credential.GetField("region") ?? throw new InvalidOperationException("The AWS Bedrock connection has no 'region'.");
            var model = credential.GetField("model") ?? throw new InvalidOperationException("The AWS Bedrock connection names no model: add a 'model' to it.");
            return new BedrockLlmClient(accessKey, secret, region, model, loggers.CreateLogger<BedrockLlmClient>());
        }

        return await factory.CreateAsync(credentialId, null, FlavorOf(template), DefaultBaseUrlOf(template), ct).ConfigureAwait(false);
    }

    public async Task<AssistantModel> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Assistant.Enabled)
            throw new AssistantModelUnavailableException("assistant_disabled", "The assistant is turned off in the settings.");

        var configured = current.Assistant.CredentialsId;
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, Unset, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new AssistantModel(await CreateClientAsync(configured, cancellationToken).ConfigureAwait(false), configured);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AssistantModelUnavailableException("assistant_connection_unavailable", $"The model connection chosen for the assistant in the settings cannot be used: {ex.Message}");
            }
        }

        LlmReplacementCredential credential;
        try
        {
            credential = await connections.ResolveCredentialAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmReplacementException ex)
        {
            throw new AssistantModelUnavailableException(ex.Code, ex.Code == "no_llm_credential" ? "The assistant needs a model connection with a key. Add one in the credentials, then try again." : ex.Message);
        }

        return new AssistantModel(await CreateClientAsync(credential.Id, cancellationToken).ConfigureAwait(false), credential.Name);
    }
}
