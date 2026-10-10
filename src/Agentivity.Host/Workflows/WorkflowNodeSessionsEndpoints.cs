using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Workflows;

/// <summary>
/// Node session endpoints for workflows.
/// Routes: /api/v1/workflow-node-sessions
/// </summary>
public static class WorkflowNodeSessionsEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowNodeSessionsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var v1 = endpoints.MapV1();

        // List sessions for a configured node session resource
        v1.MapPost(
            "/workflow-node-sessions/list",
            async (NodeSessionResourceRequest request, IEnumerable<Node> nodes, CancellationToken ct) =>
            {
                if (!TryResolveNodeSessionAdmin(request, nodes, out var admin, out var binding, out var errorResult))
                {
                    return errorResult;
                }

                var sessions = await admin.ListAsync(binding, ct).ConfigureAwait(false);
                return Results.Ok(sessions);
            }
        );

        // Inspect a specific session
        v1.MapPost(
            "/workflow-node-sessions/open",
            async (NodeSessionOpenApiRequest request, IEnumerable<Node> nodes, CancellationToken ct) =>
            {
                if (!TryResolveNodeSession(request, nodes, out var session, out var binding, out var errorResult))
                {
                    return errorResult;
                }

                var state = await session.OpenAsync(binding, new AgentSessionOpenRequest(request.SessionId, request.NodeId, request.ScopeHint), ct).ConfigureAwait(false);

                return Results.Ok(state);
            }
        );

        // Delete a specific session
        v1.MapPost(
            "/workflow-node-sessions/delete",
            async (NodeSessionOpenApiRequest request, IEnumerable<Node> nodes, CancellationToken ct) =>
            {
                if (!TryResolveNodeSession(request, nodes, out var session, out var binding, out var errorResult))
                {
                    return errorResult;
                }

                await session.DeleteAsync(binding, new AgentSessionOpenRequest(request.SessionId, request.NodeId, request.ScopeHint), ct).ConfigureAwait(false);

                return Results.Ok(
                    new
                    {
                        status = "deleted",
                        request.SessionId,
                        request.NodeId,
                        request.ScopeHint,
                    }
                );
            }
        );

        // Purge expired sessions for a configured node session resource
        v1.MapPost(
            "/workflow-node-sessions/purge-expired",
            async (NodeSessionResourceRequest request, IEnumerable<Node> nodes, CancellationToken ct) =>
            {
                if (!TryResolveNodeSessionAdmin(request, nodes, out var admin, out var binding, out var errorResult))
                {
                    return errorResult;
                }

                var removedCount = await admin.PurgeExpiredAsync(binding, ct).ConfigureAwait(false);
                return Results.Ok(new { removedCount });
            }
        );

        return endpoints;
    }

    private static bool TryResolveNodeSession(NodeSessionOpenApiRequest request, IEnumerable<Node> nodes, out IAgentSession session, out ResourceBinding binding, out IResult errorResult)
    {
        if (!TryResolveNodeSessionResource(request.NodeType, nodes, out session, out errorResult))
        {
            binding = default!;
            return false;
        }

        binding = CreateSessionBinding(request.NodeType, request.Inputs);
        return true;
    }

    private static bool TryResolveNodeSessionAdmin(NodeSessionResourceRequest request, IEnumerable<Node> nodes, out IAgentSessionAdmin admin, out ResourceBinding binding, out IResult errorResult)
    {
        if (!TryResolveNodeSessionResource(request.NodeType, nodes, out admin, out errorResult))
        {
            binding = default!;
            return false;
        }

        binding = CreateSessionBinding(request.NodeType, request.Inputs);
        return true;
    }

    private static bool TryResolveNodeSessionResource<TSession>(string nodeType, IEnumerable<Node> nodes, out TSession session, out IResult errorResult)
        where TSession : class
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            session = default!;
            errorResult = ApiEndpointHelpers.ApiValidationError(code: "node_session_resource_type_required", userMessage: "A node session resource type is required.", detail: "Node session resource nodeType is required.");
            return false;
        }

        var node = nodes.FirstOrDefault(candidate => string.Equals(candidate.Name, nodeType, StringComparison.OrdinalIgnoreCase));
        if (node is not TSession typedNode)
        {
            session = default!;
            errorResult = ApiEndpointHelpers.ApiValidationError(
                code: "node_session_resource_invalid",
                userMessage: "The requested node session resource is invalid or unsupported.",
                detail: $"Node session resource '{nodeType}' was not found or does not support the requested operation.",
                context: new { nodeType }
            );
            return false;
        }

        session = typedNode;
        errorResult = Results.Empty;
        return true;
    }

    private static ResourceBinding CreateSessionBinding(string nodeType, JsonObject? inputs) =>
        new(
            sourceNodeId: "session-resource",
            sourcePort: "resource",
            sourceNode: new WorkflowNodeDefinition("session-resource", nodeType, System.Text.Json.JsonSerializer.SerializeToElement(inputs ?? new JsonObject())),
            resourceType: ResourcePortCategory.AIAgentSession,
            state: new Dictionary<string, object?>()
        );
}

internal sealed record NodeSessionResourceRequest(string NodeType, JsonObject? Inputs);

internal sealed record NodeSessionOpenApiRequest(string NodeType, string SessionId, string NodeId, string? ScopeHint, JsonObject? Inputs);
