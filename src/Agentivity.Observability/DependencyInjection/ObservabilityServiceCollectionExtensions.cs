using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Bridges;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Options;
using Agentivity.Observability.Projection;
using Agentivity.Observability.Runtime;
using Agentivity.Observability.Sinks;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Observability.DependencyInjection;

public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddAgentTelemetryCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAgentTelemetryEmitter, AgentTelemetryEmitter>();
        return services;
    }

    /// <summary>
    /// Registers the unified <see cref="IRunEventBus"/> — the server↔front-end run/chat/HIL
    /// event channel.
    /// <para>
    /// IMPORTANT: telemetry/traces (<see cref="IAgentTelemetrySink"/>, <see cref="AgentTelemetryRunBridge"/>)
    /// are debugging-only and must NEVER be wired onto this bus — doing so previously caused
    /// stray llm_call_started/llm_call_completed events to be republished as
    /// TEXT_MESSAGE_START/CONTENT/END AG-UI frames on the agent run's SSE stream, leaving the
    /// front-end chat panel stuck. Do not re-register <see cref="AgentTelemetryRunBridge"/> as
    /// an <see cref="IAgentTelemetrySink"/> here.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRunEventBus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRunEventBus, DefaultRunEventBus>();
        return services;
    }

    public static IServiceCollection AddInMemoryAgentTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<InMemoryTelemetrySink>();
        services.AddSingleton<IAgentTelemetrySink>(serviceProvider => serviceProvider.GetRequiredService<InMemoryTelemetrySink>());
        return services;
    }

    public static IServiceCollection AddJsonLinesAgentTelemetry(this IServiceCollection services, Action<JsonLinesTelemetrySinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<JsonLinesTelemetrySinkOptions>();
        services.AddSingleton<JsonLinesTelemetrySink>();
        services.AddSingleton<IAgentTelemetrySink>(serviceProvider => serviceProvider.GetRequiredService<JsonLinesTelemetrySink>());
        return services;
    }

    public static IServiceCollection AddOpenTelemetryCompatibleAgentTelemetry(this IServiceCollection services, Action<OpenTelemetryCompatibleTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<OpenTelemetryCompatibleTelemetryOptions>();
        services.AddSingleton<OpenTelemetryCompatibleTelemetrySink>();
        services.AddSingleton<IAgentTelemetrySink>(serviceProvider => serviceProvider.GetRequiredService<OpenTelemetryCompatibleTelemetrySink>());
        return services;
    }

    /// <summary>
    /// Registers the trace pipeline core: <see cref="IAgentTraceEmitter"/>/<see cref="AgentTraceEmitter"/>.
    /// <para>
    /// This is a SEPARATE pipeline from <see cref="AddAgentTelemetryCore"/> — traces are not
    /// telemetry, must not share sinks/emitter with BetterStack/OTel export, and must not be
    /// routed through <c>IRunEventBus</c> (chat/SSE).
    /// </para>
    /// </summary>
    public static IServiceCollection AddAgentTraceCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAgentTraceEmitter, AgentTraceEmitter>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="DebugTraceProjectionSink"/> as an <see cref="IAgentTraceSink"/>,
    /// feeding the server's own debug trace view (<see cref="IDebugTraceProjectionTarget"/>).
    /// Requires <see cref="AddAgentTraceCore"/>.
    /// </summary>
    public static IServiceCollection AddDebugTraceProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DebugTraceProjectionSink>();
        services.AddSingleton<IAgentTraceSink>(serviceProvider => serviceProvider.GetRequiredService<DebugTraceProjectionSink>());
        return services;
    }
}
