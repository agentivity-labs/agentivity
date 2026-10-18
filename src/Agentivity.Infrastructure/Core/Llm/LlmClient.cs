using System.Text.Json.Nodes;
using Agentivity.Domain;
using Agentivity.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// A provider's client: it holds the generations of the provider's API (<see cref="Generations"/>, newest first) and speaks to
/// a model with the one that covers it — a model none covers (one that came out after this code) gets the newest. What does
/// not depend on the generation is here: the conversation, the settings a model refuses (<see cref="LlmRefusedSettings"/>) and
/// the guard against a cut-off answer.
/// </summary>
public abstract class LlmClient : ILlmClient
{
    private readonly ILlmTransport _transport;
    private readonly string _address;
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly ILogger _logger;

    private protected LlmClient(ILlmTransport transport, string address, string apiKey, string? defaultModel, ILogger logger)
    {
        _transport = transport;
        _address = address?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(address));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "gpt-4" : defaultModel;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The client of the provider that speaks <paramref name="flavor"/>, at <paramref name="baseUrl"/>.</summary>
    public static LlmClient Create(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, string? defaultModel, ILogger logger, LlmApiFlavor flavor = LlmApiFlavor.OpenAI) =>
        flavor switch
        {
            LlmApiFlavor.Anthropic => new AnthropicLlmClient(httpClientFactory, baseUrl, apiKey, defaultModel, logger),
            LlmApiFlavor.Gemini => new GeminiLlmClient(httpClientFactory, baseUrl, apiKey, defaultModel, logger),
            _ => new OpenAiLlmClient(httpClientFactory, baseUrl, apiKey, defaultModel, logger),
        };

    /// <summary>The provider, as messages name it.</summary>
    public abstract string ProviderName { get; }

    /// <summary>The generations of the provider's API, newest first.</summary>
    public abstract IReadOnlyList<LlmApiGeneration> Generations { get; }

    /// <summary>The generation that covers <paramref name="model"/>, or the newest when none does.</summary>
    public LlmApiGeneration GenerationFor(string model) => Generations.FirstOrDefault(g => g.Covers(model)) ?? Generations[0];

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

        var conversationResult = await CompleteConversationAsync(messages, tools: null, options: new LlmConversationOptions(Temperature: temperature, MaxTokens: maxTokens, ResponseSchema: responseSchema, ContextLength: contextLength), modelOverride, cancellationToken).ConfigureAwait(false);

        if (conversationResult.IsFailure)
        {
            return OperationResult<LlmResponse>.Failure(conversationResult.Error ?? "LLM completion failed.");
        }

        var response = conversationResult.Value;
        if (response is null || string.IsNullOrWhiteSpace(response.Content))
        {
            return OperationResult<LlmResponse>.Failure("LLM response did not contain content.");
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

        var model = modelOverride ?? _defaultModel;
        var generation = GenerationFor(model);

        // A setting the model refuses is not sent, whoever set it (see LlmRefusedSettings).
        var result = await LlmRefusedSettings.SendAsync(_address, model, options ?? new LlmConversationOptions(), opts => generation.SendAsync(new LlmApiCall(_transport, _address, _apiKey, model, messages, tools, opts, _logger), cancellationToken), _logger).ConfigureAwait(false);

        // A truncated call is a deviation from what the caller asked for (it set max_tokens
        // expecting the whole answer to fit) — never let it pass through as a silent Success that
        // nobody happens to check FinishReason on. See Agentivity.Domain.LlmCallException.
        if (result.IsSuccess && result.Value is { } response && IsTruncatedFinishReason(response.FinishReason))
        {
            throw new LlmCallException(code: "llm_truncated", message: $"LLM call was cut off by its max_tokens limit (finish_reason='{response.FinishReason}').", provider: ProviderName, model: model, finishReason: response.FinishReason);
        }

        return result;
    }

    internal static bool IsTruncatedFinishReason(string? finishReason) =>
        finishReason is not null && (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase) || finishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Response from an LLM completion request.
/// </summary>
public sealed record LlmResponse(string Content, int TokensUsed);
