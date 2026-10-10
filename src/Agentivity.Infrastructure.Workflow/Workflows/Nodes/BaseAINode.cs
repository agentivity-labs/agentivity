using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows.Nodes;

public abstract class BaseAINode : BaseNode
{
    private readonly ILlmClientFactory _llmClientFactory;
    private readonly ILogger _logger;

    protected BaseAINode(string name, ILlmClientFactory llmClientFactory, ILogger logger)
        : base(name)
    {
        _llmClientFactory = llmClientFactory ?? throw new ArgumentNullException(nameof(llmClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Invokes an LLM using the resource node connected to the specified port.
    /// The resource node is the client — no credential resolution needed here.
    /// </summary>
    protected async ValueTask<string> InvokeLlmAsync(NodeConfig config, WorkflowRunServices services, string prompt, LlmRequestOptions? options = null, string resourcePortName = "llm_model", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required", nameof(prompt));
        }

        var (llm, binding) = ResourceResolver.RequireToolNode<ILlmProvider>(config, resourcePortName, ResourcePortCategory.LlmModel);
        var result = await llm.CompleteAsync(binding, prompt, options, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure || string.IsNullOrWhiteSpace(result.Value?.Content))
        {
            throw new InvalidOperationException(result.Error ?? "LLM completion failed.");
        }

        return result.Value.Content;
    }

    /// <summary>
    /// Invokes an LLM conversation using the resource node connected to the specified port.
    /// Supports message history and optional tool definitions for agentic runtimes.
    /// </summary>
    protected async ValueTask<LlmConversationResponse> InvokeLlmConversationAsync(
        NodeConfig config,
        WorkflowRunServices services,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<JsonObject>? tools = null,
        LlmConversationOptions? options = null,
        string resourcePortName = "llm_model",
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one conversation message is required.", nameof(messages));
        }

        var (llm, binding) = ResourceResolver.RequireToolNode<ILlmProvider>(config, resourcePortName, ResourcePortCategory.LlmModel);
        var result = await llm.CompleteConversationAsync(binding, messages, tools, options, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure || result.Value is null)
        {
            throw new InvalidOperationException(result.Error ?? "LLM conversation failed.");
        }

        return result.Value;
    }

    /// <summary>
    /// Invokes an LLM with a direct credentials ID (legacy - prefer using resource ports).
    /// </summary>
    protected async ValueTask<string> InvokeLlmWithCredentialsAsync(string credentialsId, string prompt, LlmRequestOptions? options = null, CancellationToken cancellationToken = default) => await InvokeLlmWithCredentialsAsync(credentialsId, prompt, images: [], options, cancellationToken).ConfigureAwait(false);

    /// <summary>Same as the text-only overload, with images attached to the user turn for a vision-capable model.</summary>
    protected async ValueTask<string> InvokeLlmWithCredentialsAsync(string credentialsId, string prompt, IReadOnlyList<ImageAttachment> images, LlmRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialsId))
        {
            throw new ArgumentException("Credentials ID is required", nameof(credentialsId));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required", nameof(prompt));
        }

        var llmClient = await _llmClientFactory.CreateAsync(credentialsId, cancellationToken).ConfigureAwait(false);
        var requestOptions = options ?? new LlmRequestOptions();

        var result = await llmClient.CompleteAsync(prompt, images, requestOptions.SystemPrompt, requestOptions.Temperature ?? 0.7, requestOptions.MaxTokens ?? 2000, requestOptions.ResponseSchema, null, requestOptions.ContextLength, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure || string.IsNullOrWhiteSpace(result.Value?.Content))
        {
            throw new InvalidOperationException(result.Error ?? "LLM completion failed.");
        }

        return result.Value.Content;
    }

    protected static string ExtractJsonPayload(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return string.Empty;
        }

        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newlineIndex = trimmed.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                trimmed = trimmed[(newlineIndex + 1)..];
            }

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }

            trimmed = trimmed.Trim();
        }

        var objectIndex = trimmed.IndexOf('{');
        var arrayIndex = trimmed.IndexOf('[');
        var startIndex = (objectIndex, arrayIndex) switch
        {
            (>= 0, >= 0) => Math.Min(objectIndex, arrayIndex),
            (>= 0, < 0) => objectIndex,
            (< 0, >= 0) => arrayIndex,
            _ => -1,
        };

        if (startIndex < 0)
        {
            return string.Empty;
        }

        var json = trimmed[startIndex..];
        var stack = new Stack<char>();
        for (var i = 0; i < json.Length; i++)
        {
            var current = json[i];
            if (current == '{' || current == '[')
            {
                stack.Push(current == '{' ? '}' : ']');
            }
            else if (current == '}' || current == ']')
            {
                if (stack.Count == 0)
                {
                    return string.Empty;
                }

                var expected = stack.Pop();
                if (expected != current)
                {
                    return string.Empty;
                }

                if (stack.Count == 0)
                {
                    return json[..(i + 1)];
                }
            }
        }

        return stack.Count == 0 ? json : string.Empty;
    }

    protected static T DeserializeJsonPayload<T>(string jsonPayload, JsonSerializerOptions serializerOptions, string contextDescription)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            throw new InvalidOperationException($"LLM {contextDescription} JSON payload is empty.");
        }

        if (TryDeserialize(jsonPayload, serializerOptions, out T? result))
        {
            return result!;
        }

        var sanitized = SanitizeSpecialCharacters(jsonPayload);
        if (!ReferenceEquals(sanitized, jsonPayload) && TryDeserialize(sanitized, serializerOptions, out result))
        {
            return result!;
        }

        var trimmed = ExtractJsonPayload(sanitized);
        if (!ReferenceEquals(trimmed, sanitized) && TryDeserialize(trimmed, serializerOptions, out result))
        {
            return result!;
        }

        throw new InvalidOperationException($"Failed to parse LLM {contextDescription} JSON payload.");
    }

    protected static JsonNode ParseJsonNode(string jsonPayload, string contextDescription)
    {
        if (TryParseJson(jsonPayload, out var node) && node is not null)
        {
            return node;
        }

        var sanitized = SanitizeSpecialCharacters(jsonPayload);
        if (!ReferenceEquals(sanitized, jsonPayload) && TryParseJson(sanitized, out node) && node is not null)
        {
            return node;
        }

        var trimmed = ExtractJsonPayload(sanitized);
        if (!ReferenceEquals(trimmed, sanitized) && TryParseJson(trimmed, out node) && node is not null)
        {
            return node;
        }

        throw new InvalidOperationException($"Failed to parse LLM {contextDescription} JSON payload as a JSON node.");
    }

    private static bool TryDeserialize<T>(string json, JsonSerializerOptions serializerOptions, out T? result)
    {
        try
        {
            result = JsonSerializer.Deserialize<T>(json, serializerOptions);
            return result is not null;
        }
        catch (JsonException)
        {
            result = default;
            return false;
        }
    }

    private static bool TryParseJson(string json, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(json, documentOptions: default, nodeOptions: default);
            return node is not null;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static string SanitizeSpecialCharacters(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (SpecialCharacterReplacements.TryGetValue(ch, out var replacement))
            {
                builder.Append(replacement);
                continue;
            }

            if (IsPermittedCharacter(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsPermittedCharacter(char ch)
    {
        if (ch is '\n' or '\r' or '\t')
        {
            return true;
        }

        if (ch < 0x20)
        {
            return false;
        }

        return ch <= 0x7E;
    }

    private static readonly IReadOnlyDictionary<char, string> SpecialCharacterReplacements = new Dictionary<char, string>
    {
        ['→'] = "->",
        ['←'] = "<-",
        ['↑'] = "^",
        ['↓'] = "v",
        ['⇒'] = "=>",
        ['⇐'] = "<=",
        ['⇔'] = "<->",
        ['✓'] = "OK",
        ['✔'] = "OK",
        ['✗'] = "X",
        ['✘'] = "X",
        ['•'] = "*",
        ['◦'] = "-",
        ['○'] = "o",
        ['●'] = "*",
    };
}
