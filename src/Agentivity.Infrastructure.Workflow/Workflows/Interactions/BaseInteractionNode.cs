using System.Text.Json;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// Channel configuration resolved from a connected resource node.
/// Same pattern as LlmModel resource binding for AINode.
/// </summary>
public sealed record ChannelBinding(string ChannelType, string? Credentials, string? Respondent, ResourceBinding Properties);

public sealed record InteractionChannelDispatchResult(bool OutboundSent, IReadOnlyDictionary<string, object?> Metadata)
{
    public static InteractionChannelDispatchResult None { get; } = new(false, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
}

public interface IInteractionChannel
{
    ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default);

    ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default);
}

public static class InteractionChannelDispatcher
{
    private const string ChannelNodeTypeMetadataKey = "channel_node_type";
    private const string ChannelOutboundSentMetadataKey = "channel_outbound_sent";

    public static async ValueTask<InteractionRequest> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);

        var metadata = new Dictionary<string, object?>(request.Metadata, StringComparer.OrdinalIgnoreCase) { [ChannelNodeTypeMetadataKey] = channel.Properties.SourceNode.NodeType };

        if (NodeInstanceRegistry.GetAs<IInteractionChannel>(channel.Properties.SourceNode.NodeType) is not { } publisher)
        {
            return request with { Metadata = metadata };
        }

        var dispatch = await publisher.DispatchAsync(channel, request, cancellationToken).ConfigureAwait(false);
        metadata[ChannelOutboundSentMetadataKey] = dispatch.OutboundSent;

        foreach (var entry in dispatch.Metadata)
        {
            metadata[entry.Key] = entry.Value;
        }

        return request with
        {
            Metadata = metadata,
        };
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

        if (NodeInstanceRegistry.GetAs<IInteractionChannel>(nodeType) is not { } publisher)
        {
            return null;
        }

        return await publisher.TryResolveInboundAsync(request, inboundMessage, cancellationToken).ConfigureAwait(false);
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

/// <summary>
/// Base class for nodes that suspend workflow execution to collect external input.
/// Uses a resource port for the interaction channel (same pattern as AINode + LlmModel).
/// </summary>
public abstract class BaseInteractionNode : BaseNode
{
    private readonly InteractionStore _store;
    private readonly ILogger _logger;

    protected BaseInteractionNode(string name, InteractionStore store, ILogger logger)
        : base(name)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineResourceInputPort("channel", "Channel", ResourcePortCategory.InteractionChannel, "Interaction channel (Slack, Email, Forms, etc.)");
    }

    /// <summary>Resolves the channel configuration from the connected resource node.</summary>
    protected ChannelBinding ResolveChannel(NodeConfig config)
    {
        var binding = ResourceResolver.Require(config, "channel", ResourcePortCategory.InteractionChannel);
        // Derive channel type from the node instance (IHumanInteractionChannel.ChannelType), not from a user parameter.
        var channelTypeFromNode = NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(binding.SourceNode.NodeType)?.ChannelType;
        var channelType = channelTypeFromNode ?? binding.GetString("channel_type") ?? throw new InvalidOperationException($"Cannot determine channel type for resource node '{binding.SourceNode.NodeType}'. The node must implement IHumanInteractionChannel.");
        return new ChannelBinding(ChannelType: channelType, Credentials: binding.GetString("credentials"), Respondent: binding.GetString("respondent"), Properties: binding);
    }

    /// <summary>Builds the interaction request from node inputs and channel config.</summary>
    protected abstract InteractionRequest BuildRequest(NodeConfig config, WorkflowRunServices services, ChannelBinding channel);

    /// <summary>Transforms the interaction response into node outputs.</summary>
    protected abstract NodeResult MapResponse(InteractionResponse response);

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        // Check if we're resuming with a response
        var responseKey = $"__interaction_response_{config.Node.Id}";
        if (config.State.TryGetValue(responseKey, out var existing) && existing is InteractionResponse response)
        {
            services.PublishStatus($"Resuming with interaction response (outcome: {response.Outcome})");
            config.State.Remove(responseKey);
            return MapResponse(response);
        }

        // First execution: resolve channel and dispatch request
        var channel = ResolveChannel(config);
        var request = BuildRequest(config, services, channel);
        request = await InteractionChannelDispatcher.DispatchAsync(channel, request, cancellationToken).ConfigureAwait(false);

        services.PublishStatus($"Dispatching interaction via {channel.ChannelType}: {request.Title}");

        await _store.SaveRequestAsync(request, cancellationToken).ConfigureAwait(false);

        // Signal the engine to suspend this workflow
        throw new WorkflowSuspendedException(request);
    }
}
