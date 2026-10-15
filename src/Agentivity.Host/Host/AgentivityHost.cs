using Agentivity.Domain.Run;
using Agentivity.Host.Agentic;
using Agentivity.Host.Icons;
using Agentivity.Host.AgUi;
using Agentivity.Host.AiAssistant;
using Agentivity.Host.Capabilities;
using Agentivity.Host.Voice;
using Agentivity.Host.Configs;
using Agentivity.Host.Credentials;
using Agentivity.Host.LlmConnections;
using Agentivity.Infrastructure.DataTables.Endpoints;
using Agentivity.Host.Entities;
using Agentivity.Host.Media;
using Agentivity.Host.Metadata;
using Agentivity.Host.Executions;
using Agentivity.Host.Runs;
using Agentivity.Host.Settings;
using Agentivity.Host.Templates;
using Agentivity.Host.Workflows;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.DependencyInjection;
using Agentivity.Infrastructure.Agentic.DependencyInjection;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Core.DependencyInjection;
using Agentivity.Infrastructure.Fabric;
using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Hosting;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Interactions.Chat.DependencyInjection;
using Agentivity.Infrastructure.Interactions.DependencyInjection;
using Agentivity.Infrastructure.Interactions.Forms.DependencyInjection;
using Agentivity.Infrastructure.Modules;
using Agentivity.Infrastructure.Plugins;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflow.Catalog.DependencyInjection;
using Agentivity.Infrastructure.Workflow.Services.WorkflowManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;
using SerilogOtlpProtocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Executions.Metrics;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Host.Host;

/// <summary>
/// Production <see cref="IAgentivityHost"/> — builds the real ASP.NET Core application
/// (real DI wiring, real HTTP endpoints), applying <paramref name="storeStub"/> overrides
/// (a no-op in production, fakes in integration tests) before <c>builder.Build()</c>.
/// </summary>
public sealed class AgentivityHost : IAgentivityHost
{
    private readonly WebApplication _app;

    public IApiStub ApiStub { get; }

    public IServiceProvider Services => _app.Services;

    public AgentivityHost(IApiStub apiStub, IStoreStub storeStub, string[] args)
    {
        ApiStub = apiStub;

        var builder = WebApplication.CreateBuilder(args);

        var openTelemetryConfig = builder.Configuration.GetSection("OpenTelemetry");
        var openTelemetryEndpoint = openTelemetryConfig["Endpoint"]?.TrimEnd('/');
        var openTelemetryProtocolName = openTelemetryConfig["Protocol"] ?? "HttpProtobuf";
        var openTelemetryServiceName = openTelemetryConfig["ServiceName"] ?? builder.Environment.ApplicationName;
        var openTelemetryHeaders = openTelemetryConfig
            .GetSection("Headers")
            .GetChildren()
            .Where(section => !string.IsNullOrWhiteSpace(section.Value) && !string.Equals(section.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(section => section.Key, section => section.Value!, StringComparer.OrdinalIgnoreCase);
        var openTelemetryHeadersString = openTelemetryHeaders.Count > 0 ? string.Join(",", openTelemetryHeaders.Select(static pair => $"{pair.Key}={pair.Value}")) : null;
        var openTelemetryProtocol = openTelemetryProtocolName.Equals("Grpc", StringComparison.OrdinalIgnoreCase) ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
        var serilogOpenTelemetryProtocol = openTelemetryProtocol == OtlpExportProtocol.Grpc ? SerilogOtlpProtocol.Grpc : SerilogOtlpProtocol.HttpProtobuf;
        var openTelemetryEnabled = !string.IsNullOrWhiteSpace(openTelemetryEndpoint);
        var configuredLogsEndpoint = openTelemetryConfig["LogsEndpoint"];
        var configuredTracesEndpoint = openTelemetryConfig["TracesEndpoint"];
        var configuredMetricsEndpoint = openTelemetryConfig["MetricsEndpoint"];
        var openTelemetryLogsEndpoint =
            !string.IsNullOrWhiteSpace(configuredLogsEndpoint) ? configuredLogsEndpoint
            : openTelemetryProtocol == OtlpExportProtocol.HttpProtobuf ? $"{openTelemetryEndpoint}/v1/logs"
            : openTelemetryEndpoint;
        var openTelemetryTracesEndpoint =
            !string.IsNullOrWhiteSpace(configuredTracesEndpoint) ? configuredTracesEndpoint
            : openTelemetryProtocol == OtlpExportProtocol.HttpProtobuf ? $"{openTelemetryEndpoint}/v1/traces"
            : openTelemetryEndpoint;
        var openTelemetryMetricsEndpoint =
            !string.IsNullOrWhiteSpace(configuredMetricsEndpoint) ? configuredMetricsEndpoint
            : openTelemetryProtocol == OtlpExportProtocol.HttpProtobuf ? $"{openTelemetryEndpoint}/v1/metrics"
            : openTelemetryEndpoint;

        // Read custom Logs configuration from appsettings.json
        var logsConfig = builder.Configuration.GetSection("Logs").Get<LogsOptions>() ?? new LogsOptions();

        // Calculate absolute path for logs (relative to repository root)
        var repoRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
        if (!Directory.Exists(Path.Combine(repoRoot, "src")))
        {
            repoRoot = Directory.GetCurrentDirectory();
        }

        var logFilePath = Path.Combine(repoRoot, logsConfig.Path);
        var logDirectory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        // Parse RollingInterval from string
        var rollingInterval = logsConfig.RollingInterval switch
        {
            "Infinite" => RollingInterval.Infinite,
            "Year" => RollingInterval.Year,
            "Month" => RollingInterval.Month,
            "Day" => RollingInterval.Day,
            "Hour" => RollingInterval.Hour,
            "Minute" => RollingInterval.Minute,
            _ => RollingInterval.Day,
        };

        // ✅ Configure Serilog with calculated path
        builder.Host.UseSerilog(
            (context, services, configuration) =>
            {
                configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    // Information is the noisiest level in practice (routine "listing X" / "loading Y"
                    // chatter) without being as actionable as Debug (detailed diagnostics) or
                    // Warning/Error. MinimumLevel is a threshold and can't skip a level in the middle
                    // of the hierarchy, so this drops Information specifically while leaving every
                    // other level untouched, across all sinks below.
                    .Filter.ByExcluding(logEvent => logEvent.Level == LogEventLevel.Information)
                    .WriteTo.File(path: logFilePath, rollingInterval: rollingInterval, retainedFileCountLimit: logsConfig.RetainedFileCountLimit, outputTemplate: logsConfig.OutputTemplate, fileSizeLimitBytes: logsConfig.FileSizeLimitBytes, rollOnFileSizeLimit: logsConfig.RollOnFileSizeLimit);

                if (openTelemetryEnabled)
                {
                    configuration.WriteTo.OpenTelemetry(options =>
                    {
                        options.Endpoint = openTelemetryLogsEndpoint!;
                        options.Protocol = serilogOpenTelemetryProtocol;
                        options.Headers = openTelemetryHeaders;
                        options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = openTelemetryServiceName };
                    });
                }
            }
        );

        // Configure Kestrel to allow long-running requests (workflows can take minutes)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
        });

        builder
            .Services.AddInfrastructure(builder.Configuration)
            .AddInfrastructureWorkflow(builder.Configuration)
            .AddInteractionsInfrastructure(builder.Configuration) // registers IInteractionBridge, IInteractionThreadStore (generic, shared by all channels)
            .AddChatInfrastructure() // registers IChatAgentInvoker, ChatService
            .AddFormsInfrastructure() // registers FormsService — consumed by workflow form nodes; NOT the legacy polling API
            .AddAgentTelemetryCore()
            .AddOpenTelemetryCompatibleAgentTelemetry()
            .AddAgentTraceCore()
            .AddDebugTraceProjection()
            .AddOrchestrationCore()
            .AddAgenticInfrastructure(builder.Configuration)
            .AddSingleton<IModuleCatalog, ModuleCatalog>();

        builder.Services.AddSingleton<IFeatureCatalog>(sp => new FeatureCatalog(sp.GetRequiredService<IModuleCatalog>(), sp.GetServices<IFeatureContributor>())).AddSingleton<IFeatureDiscoveryService, FeatureDiscoveryService>();

        // Load the modules
        var moduleLoader = new ModuleLoader();
        builder.Services.AddSingleton<IModuleLoader>(moduleLoader);
        moduleLoader.LoadFromConfiguration(builder.Services, builder.Configuration);

        // Fabric layer — the crossing point for all entities: executable (agent/team/workflow,
        // resolved as IExecutable) and non-executable resources (DataTables today; future Env/
        // Memories/Secret/Scheduling resources join here too, not in AgentivityHost).
        builder.Services.AddFabric(builder.Configuration);

        builder.Services.AddAgentHost();

        if (openTelemetryEnabled)
        {
            builder
                .Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(openTelemetryServiceName))
                .WithTracing(tracing =>
                    tracing
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource("Agentivity.Observability")
                        .AddOtlpExporter(options =>
                        {
                            options.Endpoint = new Uri(openTelemetryTracesEndpoint!);
                            options.Protocol = openTelemetryProtocol;
                            options.Headers = openTelemetryHeadersString;
                        })
                )
                .WithMetrics(metrics =>
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddMeter("Agentivity.Observability")
                        .AddOtlpExporter(options =>
                        {
                            options.Endpoint = new Uri(openTelemetryMetricsEndpoint!);
                            options.Protocol = openTelemetryProtocol;
                            options.Headers = openTelemetryHeadersString;
                        })
                );
        }

        // Register plugins
        var pluginsDirectory = Agentivity.Infrastructure.Plugins.ServiceCollectionExtensions.ResolvePluginsDirectory(builder.Configuration, repoRoot);
        Log.Information("🔌 Loading plugins from: {PluginsDirectory}", pluginsDirectory);
        builder.Services.AddConfiguredWorkflowPlugins(
            builder.Configuration,
            repoRoot,
            plugin => Log.Information("✅ Registered plugin: {PluginName} v{Version} ({NodeCount} nodes)", plugin.Name, plugin.Version, plugin.GetNodeTypes().Count()),
            (directory, count) =>
            {
                if (count == 0)
                {
                    Log.Warning("⚠️ No plugins found in '{PluginsDirectory}'", directory);
                    return;
                }

                Log.Information("✅ Loaded {PluginCount} plugin(s) from '{PluginsDirectory}'", count, directory);
            }
        );

        // Agentic library disabled (e.g. the open-source edition): hide the steps that call an agent or a team of the library.
        // The step that calls a workflow stays, and so does the generic ai.node — it only needs the agent engine.
        if (!Agentivity.Infrastructure.Agentic.Features.AgenticCoreFeatures.IsLibraryEnabled(builder.Configuration))
        {
            var libraryNodeTypes = new HashSet<Type>
            {
                typeof(Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents.AgentCallNode),
                typeof(Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents.TeamNode),
            };
            foreach (var descriptor in builder.Services.Where(d => d.ImplementationType is not null && libraryNodeTypes.Contains(d.ImplementationType)).ToList())
            {
                builder.Services.Remove(descriptor);
            }
        }

        // Register credentials repository
        builder.Services.AddSingleton<ICredentialsRepository, FileCredentialsRepository>();

        // Register unified run event bus (SSE streaming for workflows, agents, teams)
        builder.Services.AddRunEventBus();

        builder.Services.AddSingleton<Agentivity.Host.Executions.ExecutionService>();
        builder.Services.AddSingleton<ExecutionRepository>();
        builder.Services.AddSingleton<Agentivity.Infrastructure.Repository.IRepository<Infrastructure.Execution.Executions.ExecutionContext, ExecutionRecord>>(sp => sp.GetRequiredService<ExecutionRepository>());
        builder.Services.AddSingleton<Agentivity.Host.Runs.RunsService>();

        // Execution persistence — flat JSON files under _cache/runs/executions/. Wrapped in
        // MetricsRecordingExecutionStore, the single choke point for "an execution just changed"
        // across every one of the ~10 call sites that mark active/paused/completed/failed — none
        // of them go through a repository, they all take IExecutionRecordStore directly.
        builder.Services.AddSingleton<DiskExecutionRecordStore>();
        builder.Services.AddSingleton<IExecutionMetricsStore, DiskExecutionMetricsStore>();
        builder.Services.AddSingleton<IExecutionMetricsRecorder, ExecutionMetricsRecorder>();
        builder.Services.AddSingleton<IExecutionRecordStore>(sp => new MetricsRecordingExecutionStore(
            sp.GetRequiredService<DiskExecutionRecordStore>(),
            sp.GetRequiredService<IExecutionMetricsRecorder>()
        ));

        // Register shared InteractionStore (used by workflows, agents, teams and chat).
        // Default path is defined in WaitingRunStorageOptions.StoragePath (_cache/runs/workflows).
        builder.Services.Configure<Infrastructure.Options.WaitingRunStorageOptions>(builder.Configuration.GetSection("SuspendedRuns"));
        // Register icon service
        builder.Services.AddSingleton<EmbeddedIconService>();

        // AG-UI widget bundle registry — platform-managed, registered at startup
        var widgetBundleRegistry = new AgUiWidgetBundleRegistry();
        foreach (var bundle in Agentivity.Host.AgUi.AgUiWidgetCatalog.AllBundles())
            widgetBundleRegistry.Register(bundle);
        builder.Services.AddSingleton<IAgUiWidgetBundleRegistry>(widgetBundleRegistry);

        // What the platform can do, discovered from the loaded nodes, widgets, team engine and agent engine
        builder.Services.AddCapabilityRegistry();

        // Configure workflow run retention
        builder.Services.Configure<Infrastructure.Options.WorkflowRunRetentionOptions>(builder.Configuration.GetSection("WorkflowRunRetention"));

        // Generic registry + durable-record eviction (all run types: workflow, agent, team)
        builder.Services.AddHostedService<Infrastructure.Execution.ExecutionCleanupService>();
        // Startup run recovery: re-arm waiting runs, mark crash-orphaned runs Interrupted
        builder.Services.AddHostedService<Runs.RunRecoveryService>();

        // Global exception handler — turns unhandled exceptions into ApiErrorResponse payloads
        // so endpoint handlers no longer need defensive try/catch (Exception) blocks.
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.CustomSchemaIds(type => type.FullName?.Replace("+", ".") ?? type.Name);
        });

        // Which web pages may call the API (ApiCors).
        builder.Services.AddApiCors(builder.Configuration);

        storeStub.Configure(builder.Services);

        _app = builder.Build();

        // Build the node factory now, not at the first workflow: resolving it installs it where agent code looks nodes up
        // (NodeInstanceRegistry), and an agent can run before any workflow does.
        _ = _app.Services.GetRequiredService<INodeFactory>();

        _app.UseApiCors();

        _app.UseExceptionHandler();

        _app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                diagnosticContext.Set("RequestProtocol", httpContext.Request.Protocol);
                diagnosticContext.Set("RequestTraceIdentifier", httpContext.TraceIdentifier);

                var routeValues = httpContext.GetRouteData()?.Values;
                if (routeValues is null)
                {
                    return;
                }

                if (routeValues.TryGetValue("runId", out var runId) && runId is not null)
                {
                    diagnosticContext.Set("WorkflowRunId", runId);
                }

                if (routeValues.TryGetValue("workflowId", out var workflowId) && workflowId is not null)
                {
                    diagnosticContext.Set("WorkflowId", workflowId);
                }

                if (routeValues.TryGetValue("nodeId", out var nodeId) && nodeId is not null)
                {
                    diagnosticContext.Set("NodeId", nodeId);
                }
            };
        });

        _app.MapWorkflowPluginCapabilities();
        if (Agentivity.Infrastructure.Agentic.Features.AgenticCoreFeatures.IsLibraryEnabled(_app.Configuration))
        {
            _app.MapAgenticLibraryApi();
            _app.MapAgentsApi();
            _app.MapTeamsApi();
        }
        else
        {
            Log.Information("Agentic library (agents/teams) is disabled by configuration (Features:AgenticLibrary:Enabled=false)");
        }
        _app.MapIconsApi();
        _app.MapRunsApi();
        _app.MapRunMetricsApi();
        _app.MapExecutionsApi();
        _app.MapModuleCatalogEndpoints();
        _app.MapWorkflowsApi();
        _app.MapWorkflowNodeSessionsApi();
        _app.MapExecutableEntitiesApi();
        _app.MapDataTablesApi();
        _app.MapRunInspectorApi();
        _app.MapUnifiedExecutionStreamApi();
        _app.MapAgUiWidgetBundleApi();
        _app.MapMetadataApi();
        _app.MapCapabilitiesApi();
        _app.MapCredentialsApi();
        _app.MapLlmConnectionsApi();
        _app.MapTemplatesApi();
        _app.MapConfigsApi();
        _app.MapAiAssistantApi();
        _app.MapVoiceApi();
        _app.MapSettingsApi();
        _app.MapResourcesApi();
        _app.MapUploadsApi();
        moduleLoader.RegisterLoadedModules(_app.Services.GetRequiredService<IModuleCatalog>());
        moduleLoader.MapEndpoints(_app);
        MapStudio(_app);

        // Eagerly populate the object identity registry at startup (agents, teams, workflows)
        WarmUpObjectIdentityRegistryAsync(_app.Services).GetAwaiter().GetResult();

        if (_app.Environment.IsDevelopment())
        {
            _app.UseSwagger();
            _app.UseSwaggerUI();
        }

        Log.Information("✅ Agentivity API started successfully");
    }

    /// <summary>
    /// Serves the Studio (compiled Flutter web) from a <c>wwwroot</c> folder next to the binaries, when there is one. The Docker images ship it
    /// there so one container offers the API and its Studio; a checkout has no such folder, so nothing changes. Paths under /api keep
    /// answering 404 instead of the Studio page.
    /// </summary>
    private static void MapStudio(WebApplication app)
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            return;
        }

        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        // The Studio routes itself in the browser: any other GET that matched nothing gets its page. (Not an endpoint: a catch-all
        // endpoint would be picked before the static files get a chance to answer.)
        var index = files.GetFileInfo("index.html");
        app.Use(async (context, next) =>
        {
            await next();
            if (context.Response.StatusCode == StatusCodes.Status404NotFound && !context.Response.HasStarted && HttpMethods.IsGet(context.Request.Method) && !context.Request.Path.StartsWithSegments("/api") && index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(index);
            }
        });
        Log.Information("Studio is served from {WebRoot}", webRoot);
    }

    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);

    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);

    /// <summary>Runs the application until shutdown (equivalent to <c>WebApplication.RunAsync</c>).</summary>
    public Task RunAsync() => _app.RunAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);

    private static async Task WarmUpObjectIdentityRegistryAsync(IServiceProvider services)
    {
        try
        {
            var logger = services.GetRequiredService<ILogger<AgentivityHost>>();
            var agenticRepo = services.GetService<Infrastructure.Agentic.IAgenticRepository>();
            var workflowRepo = services.GetService<Infrastructure.Repository.Entities.IEntityRepository<WorkflowEntity>>();

            if (agenticRepo is not null)
            {
                await agenticRepo.ListAgentsAsync(cancellationToken: default).ConfigureAwait(false);
                await agenticRepo.ListTeamsAsync(cancellationToken: default).ConfigureAwait(false);
            }

            if (workflowRepo is not null)
            {
                await workflowRepo.ListAsync(cancellationToken: default).ConfigureAwait(false);
            }

            logger.LogInformation("✅ Object identity registry warmed up");
        }
        catch (Exception ex)
        {
            var logger = services.GetService<ILogger<AgentivityHost>>();
            logger?.LogWarning(ex, "⚠️ Failed to warm up object identity registry — registry will be populated lazily");
        }
    }
}
