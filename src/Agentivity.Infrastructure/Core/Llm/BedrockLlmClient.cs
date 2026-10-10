using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain;
using Agentivity.Domain.Common;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// AWS Bedrock client for Anthropic Claude models. Reuses <see cref="GenericLlmClient"/>'s own
/// Anthropic Messages payload building/response parsing (<see cref="GenericLlmClient.BuildAnthropicPayload"/>,
/// <see cref="GenericLlmClient.ParseAnthropicResponse"/>) — Bedrock's InvokeModel body/response for
/// an Anthropic model is the exact same Anthropic Messages JSON, minus the top-level "model" field
/// (the model is addressed via the Bedrock modelId instead) plus an added "anthropic_version"
/// field. What's genuinely different — and the whole reason this isn't just another
/// <see cref="LlmApiFlavor"/> on <see cref="GenericLlmClient"/> — is the transport: AWS SigV4
/// request signing (access key + secret + region) via the official AWS SDK
/// (<c>AWSSDK.BedrockRuntime</c>) instead of a bearer-token HTTP call. Re-implementing SigV4 by
/// hand was explicitly rejected (EPIC-0462, P1-6) — it is a well-known source of subtle,
/// hard-to-detect security bugs when hand-rolled.
/// </summary>
public sealed class BedrockLlmClient : ILlmClient, IDisposable
{
    // Distinct from GenericLlmClient's native-API "anthropic-version" HTTP header value —
    // Bedrock's Anthropic models expect this exact string in the request BODY instead.
    private const string BedrockAnthropicVersion = "bedrock-2023-05-31";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly AmazonBedrockRuntimeClient _bedrockClient;
    private readonly string _defaultModelId;
    private readonly ILogger _logger;

    public BedrockLlmClient(string accessKeyId, string secretAccessKey, string region, string defaultModelId, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModelId);

        var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        _bedrockClient = new AmazonBedrockRuntimeClient(credentials, RegionEndpoint.GetBySystemName(region));
        _defaultModelId = defaultModelId;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<OperationResult<LlmResponse>> CompleteAsync(string prompt, string? systemPrompt = null, double temperature = 0.7, int? maxTokens = null, string? responseSchema = null, string? modelOverride = null, int? contextLength = null, CancellationToken cancellationToken = default) =>
        CompleteAsync(prompt, images: [], systemPrompt, temperature, maxTokens, responseSchema, modelOverride, contextLength, cancellationToken);

    public async Task<OperationResult<LlmResponse>> CompleteAsync(string prompt, IReadOnlyList<ImageAttachment> images, string? systemPrompt = null, double temperature = 0.7, int? maxTokens = null, string? responseSchema = null, string? modelOverride = null, int? contextLength = null, CancellationToken cancellationToken = default)
    {
        var messages = new List<LlmChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new LlmChatMessage("system", systemPrompt));
        }

        messages.Add(new LlmChatMessage("user", prompt, Images: images.Count > 0 ? images : null));

        var conversationResult = await CompleteConversationAsync(messages, tools: null, options: new LlmConversationOptions(Temperature: temperature, MaxTokens: maxTokens, ResponseSchema: responseSchema), modelOverride, cancellationToken).ConfigureAwait(false);

        if (conversationResult.IsFailure)
        {
            return OperationResult<LlmResponse>.Failure(conversationResult.Error ?? "Bedrock completion failed.");
        }

        var response = conversationResult.Value;
        if (response is null || string.IsNullOrWhiteSpace(response.Content))
        {
            return OperationResult<LlmResponse>.Failure("Bedrock response did not contain content.");
        }

        return OperationResult<LlmResponse>.Success(new LlmResponse(response.Content, response.TokensUsed));
    }

    public async Task<OperationResult<LlmConversationResponse>> CompleteConversationAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools = null, LlmConversationOptions? options = null, string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return OperationResult<LlmConversationResponse>.Failure("At least one chat message is required.");
        }

        var model = modelOverride ?? _defaultModelId;
        var opts = options ?? new LlmConversationOptions();

        var bodyResult = BuildRequestBody(model, messages, tools, opts);
        if (bodyResult.IsFailure || bodyResult.Value is null)
        {
            return OperationResult<LlmConversationResponse>.Failure(bodyResult.Error ?? "Failed to build Bedrock/Anthropic payload.");
        }

        var toolNames = tools is { Count: > 0 } ? tools.Select(LlmToolDefinitionJson.GetRequiredName).ToList() : null;
        var requestJson = JsonSerializer.Serialize(bodyResult.Value, SerializerOptions);

        _logger.LogDebug("[LLM][Bedrock] --> model={Model} chars={Chars}", model, requestJson.Length);
        _logger.LogDebug("[LLM][Bedrock] --> payload{NewLine}{Payload}", Environment.NewLine, requestJson);

        var result = await InvokeAsync(model, requestJson, toolNames, cancellationToken).ConfigureAwait(false);

        // Same truncation guard every other flavor gets in GenericLlmClient — a cut-off response
        // is a deviation from what the caller asked for, never a silent Success.
        if (result.IsSuccess && result.Value is { } response && GenericLlmClient.IsTruncatedFinishReason(response.FinishReason))
        {
            throw new LlmCallException(code: "llm_truncated", message: $"Bedrock call was cut off by its max_tokens limit (finish_reason='{response.FinishReason}').", provider: "Bedrock", model: model, finishReason: response.FinishReason);
        }

        return result;
    }

    /// <summary>
    /// Builds the exact JSON body sent to Bedrock's InvokeModel for an Anthropic model — reuses
    /// <see cref="GenericLlmClient.BuildAnthropicPayload"/> and adjusts the two fields that differ
    /// from the native Anthropic API (see <see cref="BedrockAnthropicVersion"/>). Kept separate
    /// from the actual AWS SDK call so it's unit-testable without any network access or real AWS
    /// credentials — the SigV4/transport layer is what genuinely needs the SDK, not this.
    /// </summary>
    internal static OperationResult<Dictionary<string, object?>> BuildRequestBody(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
    {
        var payloadResult = GenericLlmClient.BuildAnthropicPayload(model, messages, tools, options);
        if (payloadResult.IsFailure || payloadResult.Value is null)
        {
            return payloadResult;
        }

        var body = payloadResult.Value;
        // Bedrock addresses the model via ModelId on the request itself, not a body field — and
        // needs its OWN version marker in the body instead of GenericLlmClient's native-API HTTP
        // header ("anthropic-version").
        body.Remove("model");
        body["anthropic_version"] = BedrockAnthropicVersion;

        // Native structured outputs (output_config.format) are enabled for the direct Anthropic API only.
        // Bedrock also serves models that have none, and unlike the direct client this transport has no
        // retry-without-schema safety net — a rejection would turn a call that works into a failure.
        // Enable it here once it has been verified per model.
        body.Remove("output_config");

        return OperationResult<Dictionary<string, object?>>.Success(body);
    }

    private async Task<OperationResult<LlmConversationResponse>> InvokeAsync(string modelId, string requestJson, IReadOnlyList<string>? toolNames, CancellationToken cancellationToken)
    {
        using var requestStream = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var request = new InvokeModelRequest { ModelId = modelId, ContentType = "application/json", Accept = "application/json", Body = requestStream };

        try
        {
            InvokeModelResponse response = await _bedrockClient.InvokeModelAsync(request, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(response.Body);
            var responseJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("[LLM][Bedrock] <-- model={Model}", modelId);
            _logger.LogDebug("[LLM][Bedrock] <-- response{NewLine}{Body}", Environment.NewLine, responseJson);

            return GenericLlmClient.ParseAnthropicResponse(responseJson, toolNames);
        }
        catch (AmazonBedrockRuntimeException ex)
        {
            _logger.LogError(ex, "Bedrock InvokeModel failed for model {Model}", modelId);
            return OperationResult<LlmConversationResponse>.Failure($"Bedrock API error: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException ex)
        {
            _logger.LogError(ex, "Bedrock request failed for model {Model}", modelId);
            return OperationResult<LlmConversationResponse>.Failure($"Bedrock request failed: {ex.Message}");
        }
    }

    public void Dispose() => _bedrockClient.Dispose();
}
