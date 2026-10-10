using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflows.Interactions;

public interface IHumanInteractionChannel
{
    /// <summary>
    /// The canonical channel type identifier declared by this node (e.g. "chat", "slack", "email").
    /// Injected into <see cref="ChannelBinding.ChannelType"/> by the resolver — consumers must not
    /// derive it from the node type string.
    /// </summary>
    string ChannelType { get; }

    IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations();

    ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default);

    ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default);
}

public sealed record AgentHumanLoopCapabilities(IReadOnlyList<JsonObject> ToolDefinitions, IReadOnlyList<string> InternalInstructions)
{
    public static AgentHumanLoopCapabilities None { get; } = new([], []);

    public bool SupportsAskHuman => ToolDefinitions.Count > 0;
}

public interface IAgentHumanLoopCapabilityProvider
{
    /// <param name="allowsAskMode">
    /// Whether the requesting agent is allowed to wait for a human reply at all (see
    /// <c>AgentConfig.AllowsHumanQuestions</c>). When <see langword="false"/>, implementations must
    /// not expose a way to suspend the run for a reply — e.g. by trimming an "ask"/wait choice out
    /// of a tool's own schema — rather than merely instructing the model not to pick it.
    /// </param>
    AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true);
}

public enum AgentHumanLoopResultMode
{
    FreeText,
    Json,
}

public static class AgentHumanLoopToolDefinitionJson
{
    private const string HumanLoopPropertyName = "humanLoop";
    private const string InteractionKindPropertyName = "interactionKind";
    private const string OperationPropertyName = "operation";
    private const string ResultModePropertyName = "resultMode";
    private const string DefaultTitlePropertyName = "defaultTitle";
    private const string QuestionArgumentPropertyName = "questionArgument";
    private const string QuestionsToAskArgumentPropertyName = "questionsToAskArgument";
    private const string ContextArgumentPropertyName = "contextArgument";
    private const string TitleArgumentPropertyName = "titleArgument";
    private const string DescriptionArgumentPropertyName = "descriptionArgument";
    private const string FieldsArgumentPropertyName = "fieldsArgument";
    private const string ContentBlocksArgumentPropertyName = "contentBlocksArgument";
    private const string ModeArgumentPropertyName = "modeArgument";

    public static JsonObject Create(
        string name,
        string description,
        string inputSchema,
        InteractionKind interactionKind,
        HumanInteractionOperation operation,
        AgentHumanLoopResultMode resultMode,
        string? defaultTitle = null,
        string? questionArgumentName = null,
        string? questionsToAskArgumentName = null,
        string? contextArgumentName = null,
        string? titleArgumentName = null,
        string? descriptionArgumentName = null,
        string? fieldsArgumentName = null,
        string? contentBlocksArgumentName = null,
        string? modeArgumentName = null
    )
    {
        var tool = LlmToolDefinitionJson.Create(name, description, inputSchema);
        tool[HumanLoopPropertyName] = new JsonObject
        {
            [InteractionKindPropertyName] = interactionKind.ToString(),
            [OperationPropertyName] = operation.ToString(),
            [ResultModePropertyName] = resultMode.ToString(),
            [DefaultTitlePropertyName] = defaultTitle,
            [QuestionArgumentPropertyName] = questionArgumentName,
            [QuestionsToAskArgumentPropertyName] = questionsToAskArgumentName,
            [ContextArgumentPropertyName] = contextArgumentName,
            [TitleArgumentPropertyName] = titleArgumentName,
            [DescriptionArgumentPropertyName] = descriptionArgumentName,
            [FieldsArgumentPropertyName] = fieldsArgumentName,
            [ContentBlocksArgumentPropertyName] = contentBlocksArgumentName,
            [ModeArgumentPropertyName] = modeArgumentName,
        };

        return tool;
    }

    public static bool IsHumanLoopTool(JsonObject tool) => tool.TryGetPropertyValue(HumanLoopPropertyName, out var humanLoopNode) && humanLoopNode is JsonObject;

    public static InteractionKind GetInteractionKind(JsonObject tool) => Enum.TryParse<InteractionKind>(GetHumanLoopString(tool, InteractionKindPropertyName), ignoreCase: true, out var interactionKind) ? interactionKind : InteractionKind.Input;

    public static HumanInteractionOperation GetOperation(JsonObject tool) => Enum.TryParse<HumanInteractionOperation>(GetHumanLoopString(tool, OperationPropertyName), ignoreCase: true, out var operation) ? operation : HumanInteractionOperation.SendAndWait;

    public static AgentHumanLoopResultMode GetResultMode(JsonObject tool) => Enum.TryParse<AgentHumanLoopResultMode>(GetHumanLoopString(tool, ResultModePropertyName), ignoreCase: true, out var resultMode) ? resultMode : AgentHumanLoopResultMode.FreeText;

    public static string? GetDefaultTitle(JsonObject tool) => GetHumanLoopString(tool, DefaultTitlePropertyName);

    public static string? GetQuestionArgumentName(JsonObject tool) => GetHumanLoopString(tool, QuestionArgumentPropertyName);

    public static string? GetQuestionsToAskArgumentName(JsonObject tool) => GetHumanLoopString(tool, QuestionsToAskArgumentPropertyName);

    public static string? GetContextArgumentName(JsonObject tool) => GetHumanLoopString(tool, ContextArgumentPropertyName);

    public static string? GetTitleArgumentName(JsonObject tool) => GetHumanLoopString(tool, TitleArgumentPropertyName);

    public static string? GetDescriptionArgumentName(JsonObject tool) => GetHumanLoopString(tool, DescriptionArgumentPropertyName);

    public static string? GetFieldsArgumentName(JsonObject tool) => GetHumanLoopString(tool, FieldsArgumentPropertyName);

    /// <summary>
    /// Name of the argument holding an ordered content-block array (e.g. <c>interact_with_human</c>'s
    /// <c>content</c>), for tools whose human-facing text isn't a single flat "question" argument.
    /// When set, <see cref="HumanLoopHelper.CreateHumanLoopToolCall"/> wraps the question as a single
    /// text block in this array instead of using <see cref="GetQuestionArgumentName"/>.
    /// </summary>
    public static string? GetContentBlocksArgumentName(JsonObject tool) => GetHumanLoopString(tool, ContentBlocksArgumentPropertyName);

    /// <summary>Name of the argument declaring send-vs-wait mode (e.g. <c>interact_with_human</c>'s <c>mode</c>).</summary>
    public static string? GetModeArgumentName(JsonObject tool) => GetHumanLoopString(tool, ModeArgumentPropertyName);

    private static string? GetHumanLoopString(JsonObject tool, string propertyName)
    {
        if (!tool.TryGetPropertyValue(HumanLoopPropertyName, out var humanLoopNode) || humanLoopNode is not JsonObject humanLoop)
        {
            return null;
        }

        if (!humanLoop.TryGetPropertyValue(propertyName, out var rawNode) || rawNode is null)
        {
            return null;
        }

        return rawNode.GetValueKind() == JsonValueKind.String ? rawNode.GetValue<string>() : rawNode.ToJsonString();
    }
}

public sealed record HumanInteractionExecutionResult(bool AwaitResponse, InteractionResponse? Response, IReadOnlyDictionary<string, object?> Metadata)
{
    public static HumanInteractionExecutionResult Waiting(IReadOnlyDictionary<string, object?>? metadata = null) => new(true, null, metadata ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

    public static HumanInteractionExecutionResult Completed(InteractionResponse response, IReadOnlyDictionary<string, object?>? metadata = null) => new(false, response, metadata ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
}

public sealed record HumanInteractionDispatchResult(InteractionRequest Request, bool AwaitResponse, InteractionResponse? Response);

public static class HumanInteractionChannelDispatcher
{
    private const string ChannelNodeTypeMetadataKey = "channel_node_type";
    private const string ChannelOutboundSentMetadataKey = "channel_outbound_sent";

    public static AgentHumanLoopCapabilities ResolveAgentHumanLoopCapabilities(ChannelBinding? channel, bool allowsAskMode = true)
    {
        if (channel is null)
        {
            return AgentHumanLoopCapabilities.None;
        }

        var provider = NodeInstanceRegistry.GetAs<IAgentHumanLoopCapabilityProvider>(channel.Properties.SourceNode.NodeType);
        if (provider is not null)
        {
            return provider.GetAgentHumanLoopCapabilities(channel, allowsAskMode);
        }

        var humanInteractionChannel = NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(channel.Properties.SourceNode.NodeType);
        if (humanInteractionChannel is not null)
        {
            // The generic fallback tool below (ask_human) has no "send"-only shape — it is
            // inherently always SendAndWait. There is nothing safe to offer when the agent isn't
            // allowed to wait for a reply, so offer no HIL tool at all rather than a restricted one.
            if (!allowsAskMode)
                return AgentHumanLoopCapabilities.None;

            var supportedOperations = humanInteractionChannel.GetSupportedOperations();
            var supportsAskHuman = supportedOperations.Contains(HumanInteractionOperation.SendAndWait) || supportedOperations.Contains(HumanInteractionOperation.Receive);

            if (supportsAskHuman)
            {
                return CreateDefaultFreeTextHumanLoopCapabilities();
            }
        }

        return AgentHumanLoopCapabilities.None;
    }

    private static AgentHumanLoopCapabilities CreateDefaultFreeTextHumanLoopCapabilities() =>
        new(
            [
                AgentHumanLoopToolDefinitionJson.Create(
                    name: "ask_human",
                    description: "Ask a human for clarification, approval, or missing information when the task cannot be completed safely or confidently on your own.",
                    inputSchema: """
                    {
                      "type": "object",
                      "properties": {
                        "question": { "type": "string" },
                        "context": { "type": "string" }
                      },
                      "required": ["question"],
                      "additionalProperties": false
                    }
                    """,
                    interactionKind: InteractionKind.Input,
                    operation: HumanInteractionOperation.SendAndWait,
                    resultMode: AgentHumanLoopResultMode.FreeText,
                    defaultTitle: "Clarification needed",
                    questionArgumentName: "question",
                    contextArgumentName: "context"
                ),
            ],
            [
                "A human-in-the-loop channel is connected.",
                "Use the available human-loop tool when human input is required instead of asking follow-up questions in normal assistant text.",
                "Write concise, natural, human-readable questions.",
                "Use the optional context only for short background that helps the human answer more precisely.",
            ]
        );

    public static async ValueTask<HumanInteractionDispatchResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);

        var metadata = new Dictionary<string, object?>(request.Metadata, StringComparer.OrdinalIgnoreCase) { [ChannelNodeTypeMetadataKey] = channel.Properties.SourceNode.NodeType };

        if (NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(channel.Properties.SourceNode.NodeType) is not { } handler)
        {
            throw new InvalidOperationException($"Channel node '{channel.Properties.SourceNode.NodeType}' does not implement generic human interaction operations.");
        }

        var supportedOperations = handler.GetSupportedOperations();
        if (!supportedOperations.Contains(request.Operation))
        {
            throw new InvalidOperationException($"Channel '{channel.ChannelType}' does not support human interaction operation '{request.Operation}'.");
        }

        var execution = await handler.ExecuteAsync(channel, request, cancellationToken).ConfigureAwait(false);
        metadata[ChannelOutboundSentMetadataKey] = !execution.AwaitResponse || request.Operation != HumanInteractionOperation.Receive;

        foreach (var entry in execution.Metadata)
        {
            metadata[entry.Key] = entry.Value;
        }

        return new HumanInteractionDispatchResult(request with { Metadata = metadata }, execution.AwaitResponse, execution.Response);
    }

    public static async ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inboundMessage);

        var nodeType = GetMetadataString(request.Metadata, ChannelNodeTypeMetadataKey);
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return null;
        }

        if (NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(nodeType) is not { } handler)
        {
            return null;
        }

        return await handler.TryResolveInboundAsync(request, inboundMessage, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetMetadataString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var rawValue) || rawValue is null)
        {
            return null;
        }

        return rawValue switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement { ValueKind: JsonValueKind.String } jsonString => jsonString.GetString(),
            _ => rawValue.ToString(),
        };
    }
}
