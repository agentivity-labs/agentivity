using Agentivity.Infrastructure.Interactions.Chat.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Interactions.Chat.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers chat-specific services: <see cref="IChatAgentInvoker"/>, <see cref="ChatService"/>.
    /// Requires <c>AddInteractionsInfrastructure()</c> to have been called first.
    /// </summary>
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IChatAgentInvoker, UnconfiguredChatAgentInvoker>();
        services.TryAddSingleton<ChatService>();
        return services;
    }
}
