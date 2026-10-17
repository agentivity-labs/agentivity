using Agentivity.Domain.Workflow;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools;

public sealed class McpServerNode : ResourceNode, IAgentTool
{
    private readonly IHttpClientFactory _httpClientFactory;

    // The connection to the remote server for the run this node was built for — its remote session id and its cached
    // tool list. A connection is bound to the run that opened it: it reads and writes that run's own state (the remote
    // session id, so a run that suspends and resumes finds its session again) through the resource binding it was built
    // from. It must therefore never be shared across runs, which is why it lives on this instance — made for one agent
    // run and dropped with it — and not in a cache for the whole application.
    private readonly ConcurrentDictionary<string, HttpMcpToolBridge> _bridges = new(StringComparer.OrdinalIgnoreCase);

    public McpServerNode(IHttpClientFactory httpClientFactory)
        : base("ai.tools.mcp.server")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("MCP Server", "Connects AI agents to a remote MCP server over HTTP.", "ai.tools", whenToUse: "To give an agent the tools of an outside service that offers an MCP server: every tool that server lists becomes available to the agent. You need the server's address (URL), and a token when the service asks for one.");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "MCP Server", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineInput(
            "server_url",
            ArgumentType.Text,
            "Remote MCP HTTP endpoint URL",
            ui: new ArgumentUiMetadata
            {
                Tab = "Connection",
                Group = "Server",
                Order = 10,
            },
            required: true
        );
        DefineInput(
            "bearer_token",
            ArgumentType.Text,
            "Optional bearer token",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "Connection",
                Group = "Authentication",
                Order = 20,
            },
            required: false
        );
        DefineInput(
            "headers_json",
            ArgumentType.Text,
            "Optional extra HTTP headers as a JSON object",
            "{}",
            ui: new ArgumentUiMetadata
            {
                Tab = "Connection",
                Group = "Authentication",
                Order = 30,
                Multiline = true,
                Rows = 6,
                Format = "json",
            },
            required: false
        );
        DefineInput(
            "protocol_version",
            ArgumentType.Text,
            "MCP protocol version",
            "2025-03-26",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Protocol",
                Order = 40,
            },
            required: false
        );
        DefineInput(
            "client_name",
            ArgumentType.Text,
            "Client name reported during MCP initialize",
            "Agentivity",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Client",
                Order = 50,
            },
            required: false
        );
        DefineInput(
            "client_version",
            ArgumentType.Text,
            "Client version reported during MCP initialize",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Client",
                Order = 60,
            },
            required: false
        );
        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "HTTP timeout in seconds",
            30,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Network",
                Order = 70,
            },
            required: false
        );
        DefineInput(
            "cache_tools",
            ArgumentType.Boolean,
            "Cache remote tool definitions during the workflow run",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Caching",
                Order = 80,
            },
            required: false
        );

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default) => GetOrCreateBridge(binding).ListToolsAsync(cancellationToken);

    public ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default) => GetOrCreateBridge(binding).CallToolAsync(toolName, arguments, cancellationToken);

    private HttpMcpToolBridge GetOrCreateBridge(ResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        string cacheKey = $"{binding.SourceNodeId}:{binding.SourcePort}:{binding.GetString("server_url")}";
        return _bridges.GetOrAdd(cacheKey, _ => new HttpMcpToolBridge(_httpClientFactory, binding));
    }
}
