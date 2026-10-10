namespace Agentivity.Infrastructure.Interactions.Chat.Services;

/// <summary>
/// Token-streaming result emitted by <see cref="IChatAgentInvoker"/>.
/// </summary>
public sealed record ChatAgentToken(string MessageId, string? Delta, bool IsFinal = false);

/// <summary>
/// Invokes the chat agent/LLM for a thread and streams tokens back to the caller.
/// Implement this interface in the agentic infrastructure layer and register it
/// via DI to enable real-time token streaming on the AG-UI chat run endpoint.
/// </summary>
public interface IChatAgentInvoker
{
    /// <summary>
    /// Runs the chat agent for the given thread and context, streaming tokens
    /// via the returned async enumerable.
    /// </summary>
    IAsyncEnumerable<ChatAgentToken> InvokeAsync(Guid contextId, Guid threadId, string runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when no <see cref="IChatAgentInvoker"/> implementation has been registered.
/// </summary>
public sealed class ChatAgentInvokerNotConfiguredException(string message) : InvalidOperationException(message);

/// <summary>
/// Fallback implementation that throws <see cref="ChatAgentInvokerNotConfiguredException"/>
/// when no real invoker has been wired up.
/// </summary>
internal sealed class UnconfiguredChatAgentInvoker : IChatAgentInvoker
{
#pragma warning disable CS1998
    public async IAsyncEnumerable<ChatAgentToken> InvokeAsync(Guid contextId, Guid threadId, string runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        throw new ChatAgentInvokerNotConfiguredException("No IChatAgentInvoker implementation has been registered. " + "Register a concrete invoker in your composition root to enable AG-UI chat streaming.");
        yield break;
    }
#pragma warning restore CS1998
}
