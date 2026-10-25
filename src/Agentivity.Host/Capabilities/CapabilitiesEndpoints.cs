using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflows.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.Capabilities;

/// <summary>
/// What the loaded platform can do, discovered at startup from the nodes, widgets, team engine and agent engine that are
/// actually present. The AI assistant reads this instead of carrying a catalog of its own.
/// <para>
/// Routes: <c>/api/v1/capabilities</c> (overview), <c>/capabilities/search</c>, <c>/capabilities/{kind}</c>,
/// <c>/capabilities/{kind}/{id}</c>.
/// </para>
/// </summary>
public static class CapabilitiesEndpoints
{
    /// <summary>Registers the contributors of the built-in subsystems and the registry that aggregates them.</summary>
    public static IServiceCollection AddCapabilityRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ICapabilityContributor, NodeCapabilityContributor>();
        services.AddSingleton<ICapabilityContributor, WidgetCapabilityContributor>();
        services.AddSingleton<ICapabilityContributor, OrchestratorCapabilityContributor>();
        services.AddSingleton<ICapabilityContributor, AgentModeCapabilityContributor>();
        services.AddSingleton<ICapabilityEmbedder, LocalCapabilityEmbedder>();
        services.AddSingleton<ICapabilityEmbeddingCache>(_ => new FileCapabilityEmbeddingCache(PathResolver.Resolve("_cache/capabilities/")));
        services.AddSingleton<ICapabilityRegistry>(sp =>
            new CapabilityRegistry(sp.GetServices<ICapabilityContributor>(), sp.GetService<ILogger<CapabilityRegistry>>(), sp.GetService<ICapabilityEmbedder>(), sp.GetService<ICapabilityEmbeddingCache>())
            {
                // How close in meaning a match must be to count. Tuned with tools/assistant-eval/search-quality.mjs.
                SemanticFloor = sp.GetRequiredService<IConfiguration>().GetValue("Capabilities:SemanticFloor", 0.25),
            }
        );
        services.AddHostedService<CapabilityRegistryWarmup>();
        return services;
    }

    public static IEndpointRouteBuilder MapCapabilitiesApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet(
            "/capabilities",
            (ICapabilityRegistry registry) =>
            {
                var snapshot = registry.Current;
                return Results.Ok(new { version = snapshot.Version, total = snapshot.Items.Count, kinds = snapshot.Overview(), problems = snapshot.Problems, semantic = registry.Semantic });
            }
        )
            .WithSummary("Get an overview of the platform capabilities");

        v1.MapGet(
            "/capabilities/search",
            async (ICapabilityRegistry registry, string? q, string? kind, int? limit, string? mode, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(q))
                    return Results.BadRequest(new { error = "Query parameter 'q' is required." });

                var snapshot = registry.Current;
                // mode=lexical forces words only, to compare what meaning adds.
                var result = await registry.SearchAsync(q, kind, Math.Clamp(limit ?? 10, 1, 50), string.Equals(mode, "lexical", StringComparison.OrdinalIgnoreCase), ct);
                return Results.Ok(
                    new
                    {
                        version = snapshot.Version,
                        query = q,
                        mode = result.Mode,
                        note = result.Note,
                        hits = result.Hits.Select(h => new
                        {
                            kind = h.Capability.Kind,
                            id = h.Capability.Id,
                            name = h.Capability.Name,
                            summary = CapabilitySnapshot.Summary(h.Capability),
                            category = h.Capability.Category,
                            score = h.Score,
                            via = h.Via,
                        }),
                    }
                );
            }
        )
            .WithSummary("Search the platform capabilities");

        v1.MapGet(
            "/capabilities/{kind}",
            (ICapabilityRegistry registry, string kind, string? category) =>
            {
                var snapshot = registry.Current;
                var items = snapshot.List(kind, category);
                return Results.Ok(
                    new
                    {
                        version = snapshot.Version,
                        kind,
                        items = items.Select(i => new
                        {
                            id = i.Id,
                            name = i.Name,
                            summary = CapabilitySnapshot.Summary(i),
                            category = i.Category,
                        }),
                    }
                );
            }
        )
            .WithSummary("List the capabilities of one kind");

        v1.MapGet(
            "/capabilities/{kind}/{id}",
            (ICapabilityRegistry registry, string kind, string id) =>
            {
                var snapshot = registry.Current;
                var item = snapshot.Get(kind, id);
                if (item is null)
                    return Results.NotFound(new { error = $"No {kind} capability named '{id}'." });

                return Results.Ok(
                    new
                    {
                        version = snapshot.Version,
                        kind = item.Kind,
                        id = item.Id,
                        name = item.Name,
                        description = item.Description,
                        whenToUse = item.WhenToUse,
                        category = item.Category,
                        card = CapabilitySnapshot.CardOf(item),
                        details = item.Details,
                    }
                );
            }
        )
            .WithSummary("Get the details of one capability");

        return routes;
    }
}

/// <summary>
/// Builds the registry once the application is up, so the platform reports what it can do at launch and a faulty
/// contributor is visible in the log at startup instead of on first use.
/// </summary>
internal sealed class CapabilityRegistryWarmup : IHostedService
{
    private readonly ICapabilityRegistry _registry;
    private readonly ILogger<CapabilityRegistryWarmup> _logger;

    public CapabilityRegistryWarmup(ICapabilityRegistry registry, ILogger<CapabilityRegistryWarmup> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _registry.Refresh();
            _logger.LogInformation(
                "Capabilities at launch: {Summary} (version {Version})",
                string.Join(", ", snapshot.Overview().Select(k => $"{k.Count} {k.Kind}")),
                snapshot.Version
            );
            foreach (var problem in snapshot.Problems)
                _logger.LogWarning("Capability registry: {Problem}", problem);

            // The meaning index is built in the background (it may have to load or download a model): until it is ready,
            // searches use words alone, and the launch does not wait.
            _ = Task.Run(
                async () =>
                {
                    var status = await _registry.PrepareSemanticAsync(CancellationToken.None);
                    if (status.Ready)
                        _logger.LogInformation("Capability search by meaning ready: {Vectors} capabilities ({Computed} computed now)", status.Vectors, status.Computed);
                    else
                        _logger.LogInformation("Capability search stays on words: {Reason}", status.Reason);
                },
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            // Discovery must never stop the platform from starting.
            _logger.LogWarning(ex, "Capability registry could not be built at launch");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
