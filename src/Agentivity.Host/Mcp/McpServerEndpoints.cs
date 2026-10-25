using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Domain.Executable;
using Agentivity.Host.Executions;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Identity;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;

namespace Agentivity.Host.Mcp;

/// <summary>
/// The instance as an MCP server, at <c>/mcp</c> (Streamable HTTP): an AI assistant finds there, as tools, the agents, teams and
/// workflows its caller may run, and runs them. A tool gives the answer of what it ran, and its named outputs.
/// <para>
/// It follows the rights of the caller, as every route does: on an instance with sign-in, a key made for MCP (scope
/// <see cref="KeyScopes.Mcp"/>); never a web page. A page of another site cannot reach it from a browser (DNS rebinding).
/// When there are more elements than <see cref="ListedToolsKey"/>, the assistant gets two tools instead, to find and to run.
/// </para>
/// </summary>
public static partial class McpServerEndpoints
{
    public const string Route = "/mcp";
    public const string ListedToolsKey = "Mcp:ListedTools";
    public const int DefaultListedTools = 50;

    private const string Find = "find";
    private const string Run = "run";

    public static IServiceCollection AddAgentivityMcp(this IServiceCollection services)
    {
        services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "Agentivity", Version = typeof(McpServerEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" })
            // Each call is answered within its HTTP request, as the caller who made it: nothing is kept between two.
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    public static WebApplication MapAgentivityMcp(this WebApplication app)
    {
        // A browser always says which page calls: one of another site is refused (an MCP client outside a browser says none).
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Route) && context.RequestServices.GetRequiredService<IEdition>() is var edition && !edition.Has(EditionFeatures.Mcp))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(EditionRefusals.Feature(edition, "the MCP server")).ConfigureAwait(false);
                return;
            }

            if (context.Request.Path.StartsWithSegments(Route) && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin && !IsOwnOrigin(context.Request, origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "origin_not_allowed", message = "The MCP server answers MCP clients, not the pages of other sites." }).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp(Route).KeyScope(KeyScopes.Mcp).WithSummary("The MCP server: the agents, teams and workflows the caller may run, as tools");
        return app;
    }

    // The address the browser used may be the one of a gateway in front (X-Forwarded-Host): a page of another site cannot
    // set that header without a preflight, so it does not open the door to it.
    private static bool IsOwnOrigin(HttpRequest request, string origin) =>
        string.Equals(origin.TrimEnd('/'), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
        || string.Equals(origin.TrimEnd('/'), $"https://{request.Host}", StringComparison.OrdinalIgnoreCase)
        || (request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } forwarded
            && string.Equals(origin.TrimEnd('/'), $"https://{forwarded}", StringComparison.OrdinalIgnoreCase));

    // ── The tools ────────────────────────────────────────────────────────────

    private static async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        var elements = await ElementsAsync(request, ct).ConfigureAwait(false);
        var listed = request.Services!.GetRequiredService<IConfiguration>().GetValue(ListedToolsKey, DefaultListedTools);
        if (elements.Count > listed)
            return new ListToolsResult { Tools = [FindTool(), RunTool()] };
        return new ListToolsResult { Tools = [.. elements.Select(e => ToolOf(e.Definition, e.Name))] };
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var name = request.Params?.Name ?? string.Empty;
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        var elements = await ElementsAsync(request, ct).ConfigureAwait(false);

        if (name == Find && !elements.Any(e => e.Name == Find))
            return FindResult(elements, Text(arguments, "query"));

        var element = name == Run && !elements.Any(e => e.Name == Run)
            ? elements.Select(e => e.Definition).FirstOrDefault(d => string.Equals(d.Id, Text(arguments, "id"), StringComparison.OrdinalIgnoreCase))
            : elements.Where(e => e.Name == name).Select(e => e.Definition).FirstOrDefault();
        if (element is null)
            return Problem(name == Run ? "There is no agent, team or workflow with this id: use find to know them." : $"There is no tool '{name}'.");

        return await RunAsync(request, element, Text(arguments, "input") ?? string.Empty, ct).ConfigureAwait(false);
    }

    // What the caller may run, each with its tool name. Every element of the instance today; the spaces will narrow it.
    private static async Task<List<(IExecutableDefinition Definition, string Name)>> ElementsAsync(MessageContext request, CancellationToken ct)
    {
        var definitions = await request.Services!.GetRequiredService<IExecutableResolver>().ListAsync(null, ct).ConfigureAwait(false);
        return [.. definitions.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => (d, NameOf(d)))];
    }

    // A tool is named after its element (letters, digits and '_', as MCP asks), with the start of its id so that two elements
    // of the same name stay two tools.
    private static string NameOf(IExecutableDefinition definition)
    {
        var words = NotAWordCharacter().Replace(definition.Name.ToLowerInvariant(), "_").Trim('_');
        if (words.Length > 48)
            words = words[..48].TrimEnd('_');
        var id = NotAWordCharacter().Replace(definition.Id.ToLowerInvariant(), string.Empty);
        return $"{(words.Length == 0 ? definition.Kind.ToString().ToLowerInvariant() : words)}_{id[..Math.Min(6, id.Length)]}";
    }

    private static Tool ToolOf(IExecutableDefinition definition, string name)
    {
        var named = definition.Outputs.Select(o => o.Name).Where(n => !string.Equals(n, NamedOutputs.Answer, StringComparison.OrdinalIgnoreCase)).ToList();
        var about = new StringBuilder($"{KindOf(definition)} \"{definition.Name}\"");
        about.Append(string.IsNullOrWhiteSpace(definition.Role) ? "." : $": {definition.Role.Trim()}");
        if (named.Count > 0)
            about.Append($" It gives its answer, and {string.Join(", ", named)}.");
        return new Tool
        {
            Name = name,
            Title = definition.Name,
            Description = about.ToString(),
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { input = new { type = "string", description = $"What to ask or give the {KindOf(definition).ToLowerInvariant()}." } },
                required = new[] { "input" },
            }),
        };
    }

    private static Tool FindTool() =>
        new()
        {
            Name = Find,
            Title = "Find an agent, a team or a workflow",
            Description = "Lists the agents, teams and workflows you may run whose name or description holds the words asked (all of them without words), with their id for run.",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string", description = "Words of what you look for." } } }),
        };

    private static Tool RunTool() =>
        new()
        {
            Name = Run,
            Title = "Run an agent, a team or a workflow",
            Description = "Runs the agent, team or workflow of this id (from find), with what you ask or give it, and gives its answer and its named outputs.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { id = new { type = "string", description = "Its id, as find gives it." }, input = new { type = "string", description = "What to ask or give it." } },
                required = new[] { "id", "input" },
            }),
        };

    private static CallToolResult FindResult(List<(IExecutableDefinition Definition, string Name)> elements, string? query)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = elements
            .Where(e => words.All(w => $"{e.Definition.Name} {e.Definition.Role}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(25)
            .Select(e => $"- {e.Definition.Name} ({KindOf(e.Definition).ToLowerInvariant()}), id {e.Definition.Id}{(string.IsNullOrWhiteSpace(e.Definition.Role) ? "" : $": {e.Definition.Role.Trim()}")}")
            .ToList();
        return new CallToolResult { Content = [new TextContentBlock { Text = found.Count == 0 ? "Nothing matches these words." : string.Join("\n", found) }] };
    }

    // Starts the element as the caller, the way the Studio does (the same execution, history and events), and waits for its answer.
    private static async Task<CallToolResult> RunAsync(MessageContext request, IExecutableDefinition element, string input, CancellationToken ct)
    {
        var services = request.Services!;
        var caller = Caller.From(request.User);
        var done = new TaskCompletionSource<(ExecutorResult? Result, Exception? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await services.GetRequiredService<ExecutionService>().StartAsync(
            new StartExecutionRequest(element.Id, input),
            services.GetRequiredService<IExecutableResolver>(),
            services.GetRequiredService<IRunEventBus>(),
            services.GetService<ITeamEngine>(),
            services.GetRequiredService<IAgenticRepository>(),
            services.GetService<WorkflowEngine>(),
            services.GetRequiredService<IRepository<ExecutionContext, ExecutionRecord>>(),
            services.GetRequiredService<ILoggerFactory>(),
            ct,
            services.GetService<IRepository<RunContext, RunRecord>>(),
            caller,
            (result, error) => done.TrySetResult((result, error))
        ).ConfigureAwait(false);

        if (started is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted })
            return Problem($"The {KindOf(element).ToLowerInvariant()} \"{element.Name}\" could not start.");
        var executionId = (started as IValueHttpResult)?.Value is { } value ? JsonSerializer.SerializeToElement(value).GetProperty("executionId").GetString() : null;

        var (answer, failure) = await done.Task.WaitAsync(ct).ConfigureAwait(false);
        if (failure is not null || answer?.Status == ExecutableStatus.Failed)
            return Problem($"The {KindOf(element).ToLowerInvariant()} \"{element.Name}\" failed: {failure?.Message ?? answer?.Content}");
        if (answer?.Status == ExecutableStatus.WaitingFor)
            return new CallToolResult { Content = [new TextContentBlock { Text = $"The {KindOf(element).ToLowerInvariant()} \"{element.Name}\" waits for a person's answer: it is asked in the Studio (execution {executionId})." }] };

        var outputs = new Dictionary<string, object?>(answer?.Outputs ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase) { [NamedOutputs.Answer] = answer?.Content };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = NamedOutputs.Describe(answer?.Content ?? string.Empty, answer?.Outputs ?? new Dictionary<string, object?>()) }],
            StructuredContent = JsonSerializer.SerializeToElement(outputs),
        };
    }

    private static CallToolResult Problem(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static string? Text(IDictionary<string, JsonElement> arguments, string key) =>
        arguments.TryGetValue(key, out var value) ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()) : null;

    private static string KindOf(IExecutableDefinition definition) =>
        definition.Kind switch
        {
            ExecutableKind.Agent => "Agent",
            ExecutableKind.Team => "Team",
            _ => "Workflow",
        };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAWordCharacter();
}
