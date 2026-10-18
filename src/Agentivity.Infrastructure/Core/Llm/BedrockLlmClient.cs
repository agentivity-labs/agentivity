using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Anthropic's Claude models on AWS Bedrock. They speak the generations of Claude (<see cref="AnthropicLlmClient.ClaudeGenerations"/>):
/// Bedrock's InvokeModel body and answer for a Claude model are the same Anthropic Messages JSON, minus the top-level
/// "model" field (the model is addressed by the Bedrock modelId) plus an "anthropic_version" field. What genuinely differs
/// is the transport: AWS SigV4 request signing (access key + secret + region) via the official AWS SDK
/// (<c>AWSSDK.BedrockRuntime</c>) instead of an HTTP call with a key. Re-implementing SigV4 by hand was explicitly rejected
/// (EPIC-0462, P1-6) — it is a well-known source of subtle, hard-to-detect security bugs when hand-rolled.
/// </summary>
public sealed class BedrockLlmClient : LlmClient, IDisposable
{
    private readonly AmazonBedrockRuntimeClient _bedrockClient;

    public BedrockLlmClient(string accessKeyId, string secretAccessKey, string region, string defaultModelId, ILogger logger)
        : this(Connect(accessKeyId, secretAccessKey, region, defaultModelId), region, defaultModelId, logger) { }

    /// <summary>Over a given Bedrock client (the tests give one that answers what they need).</summary>
    internal BedrockLlmClient(AmazonBedrockRuntimeClient bedrockClient, string region, string defaultModelId, ILogger logger)
        : base(new BedrockTransport(bedrockClient, logger), $"bedrock:{region}", apiKey: string.Empty, defaultModelId, logger)
    {
        _bedrockClient = bedrockClient;
    }

    private static AmazonBedrockRuntimeClient Connect(string accessKeyId, string secretAccessKey, string region, string defaultModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModelId);
        return new AmazonBedrockRuntimeClient(new BasicAWSCredentials(accessKeyId, secretAccessKey), RegionEndpoint.GetBySystemName(region));
    }

    public override string ProviderName => "Bedrock";

    public override IReadOnlyList<LlmApiGeneration> Generations => AnthropicLlmClient.ClaudeGenerations;

    /// <summary>
    /// The exact JSON body sent to Bedrock's InvokeModel for a Claude model: the body of the direct API
    /// (<see cref="AnthropicMessages.BuildPayload"/>) adjusted the way Bedrock takes it (see <see cref="BedrockTransport.ForBedrock"/>).
    /// Kept apart from the AWS SDK call so it is testable without any network access or real AWS credentials.
    /// </summary>
    internal static OperationResult<Dictionary<string, object?>> BuildRequestBody(string model, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options)
    {
        var payloadResult = AnthropicMessages.BuildPayload(model, messages, tools, options);
        if (payloadResult.IsFailure || payloadResult.Value is null)
        {
            return payloadResult;
        }

        return OperationResult<Dictionary<string, object?>>.Success(BedrockTransport.ForBedrock(payloadResult.Value));
    }

    public void Dispose() => _bedrockClient.Dispose();

    /// <summary>A request to a Claude model sent through Bedrock's InvokeModel, signed by the AWS SDK.</summary>
    private sealed class BedrockTransport(AmazonBedrockRuntimeClient bedrockClient, ILogger logger) : ILlmTransport
    {
        // Distinct from the direct API's "anthropic-version" HTTP header — Bedrock's Claude models expect this exact string in the request BODY instead.
        private const string BedrockAnthropicVersion = "bedrock-2023-05-31";

        /// <summary>The direct API's body, the way Bedrock takes it.</summary>
        internal static Dictionary<string, object?> ForBedrock(Dictionary<string, object?> body)
        {
            // Bedrock addresses the model via ModelId on the request itself, not a body field — and needs its OWN version
            // marker in the body instead of the direct API's HTTP header ("anthropic-version").
            body.Remove("model");
            body["anthropic_version"] = BedrockAnthropicVersion;

            // Native structured outputs (output_config.format) are enabled for the direct Anthropic API only. Bedrock also
            // serves models that have none: enable it here once it has been verified per model.
            body.Remove("output_config");
            return body;
        }

        public async Task<LlmWireReply> SendAsync(LlmWireRequest request, CancellationToken cancellationToken)
        {
            var requestJson = JsonSerializer.Serialize(ForBedrock(request.Body), HttpLlmTransport.SerializerOptions);
            logger.LogDebug("[LLM][Bedrock] --> model={Model} chars={Chars}", request.Model, requestJson.Length);
            logger.LogDebug("[LLM][Bedrock] --> payload{NewLine}{Payload}", Environment.NewLine, requestJson);

            using var requestStream = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
            var invoke = new InvokeModelRequest { ModelId = request.Model, ContentType = "application/json", Accept = "application/json", Body = requestStream };

            try
            {
                InvokeModelResponse response = await bedrockClient.InvokeModelAsync(invoke, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(response.Body);
                var responseJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                logger.LogDebug("[LLM][Bedrock] <-- model={Model}", request.Model);
                logger.LogDebug("[LLM][Bedrock] <-- response{NewLine}{Body}", Environment.NewLine, responseJson);

                return new LlmWireReply(OperationResult<string>.Success(responseJson), WasBadRequest: false);
            }
            catch (AmazonBedrockRuntimeException ex)
            {
                logger.LogError(ex, "Bedrock InvokeModel failed for model {Model}", request.Model);
                return new LlmWireReply(OperationResult<string>.Failure(LlmProviderError.Describe("Bedrock", ex.StatusCode, ex.Message)), ex.StatusCode == HttpStatusCode.BadRequest);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AmazonServiceException ex)
            {
                logger.LogError(ex, "Bedrock request failed for model {Model}", request.Model);
                return new LlmWireReply(OperationResult<string>.Failure($"Bedrock request failed: {ex.Message}"), WasBadRequest: false);
            }
        }
    }
}
