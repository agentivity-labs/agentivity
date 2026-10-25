namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Inbound message received from any interaction channel (chat, email, Slack, forms, …).
/// Channel-agnostic: the bridge uses <see cref="ChannelType"/> to route to the right handler.
/// </summary>
public sealed record InteractionChannelInboundMessage(string ChannelType, string? ExternalChannelId, string? ExternalConversationId, string? ExternalMessageId, string? ExternalUserId, string? ExternalUserName, string? Text, IReadOnlyDictionary<string, object?> Data);
