using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Metadata;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.Metadata;

/// <summary>
/// Node metadata and category discovery endpoints.
/// Routes: /api/v1/metadata/nodes, /api/v1/metadata/categories
/// </summary>
public static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet(
            "/metadata/nodes",
            (IEnumerable<Node> nodes) =>
            {
                var result = nodes
                    .OfType<INodeMetadataProvider>()
                    .Select(n =>
                    {
                        var icon = n.GetIconMetadata();
                        var meta = n.Metadata;
                        var inputsWithMeta = n.GetInputsWithMetadata();
                        var credentialsTemplateId = n.GetCredentialsTemplateId();
                        var nodeClass = ResourceNodeRegistry.GetNodeClass(n.NodeType);
                        return new
                        {
                            nodeType = n.NodeType,
                            nodeClass = nodeClass,
                            displayName = meta?.DisplayName,
                            description = meta?.Description,
                            categoryId = meta?.CategoryId,
                            whenToUse = meta?.WhenToUse,
                            example = meta?.Example,
                            // null while the node is not classified; otherwise the names of the flags that are set (empty = none).
                            effects = meta?.Effects?.ToNames(),
                            inputs = inputsWithMeta.Select(p => new
                            {
                                name = p.Name,
                                type = p.Type,
                                description = p.Description,
                                // null = not declared yet, false = optional, true = the node cannot run without it.
                                required = p.Required,
                                defaultValue = p.DefaultValue,
                                editorMode = p.EditorMode,
                                credentialsTemplateId = p.CredentialsTemplateId,
                                choices = p.Choices?.Select(c => new { value = c.Value, label = c.Label }),
                                ui = p.Ui,
                            }),
                            // Outputs that depend on the node's configuration or on what it runs; null when every output is declared.
                            dynamicOutputs = n.DynamicOutputs,
                            outputs = n.GetOutputs()
                                .Select(p => new
                                {
                                    name = p.Name,
                                    type = p.Type,
                                    description = p.Description,
                                }),
                            inputPorts = n.GetInputPorts()
                                .Where(p => p.Kind == PortKind.Data)
                                .Select(p => new
                                {
                                    name = p.Name,
                                    display = p.DisplayName,
                                    displayName = p.DisplayName,
                                    description = p.Description,
                                    color = p.Color,
                                    required = p.IsRequired,
                                    maxConnections = p.MaxConnections,
                                }),
                            outputPorts = n.GetOutputPorts()
                                .Where(p => p.Kind == PortKind.Data)
                                .Select(p => new
                                {
                                    name = p.Name,
                                    display = p.DisplayName,
                                    displayName = p.DisplayName,
                                    description = p.Description,
                                    color = p.Color,
                                    required = p.IsRequired,
                                    maxConnections = p.MaxConnections,
                                }),
                            resourceInputPorts = n.GetInputPorts()
                                .Where(p => p.Kind == PortKind.Resource)
                                .Select(p => new
                                {
                                    name = p.Name,
                                    display = p.DisplayName,
                                    displayName = p.DisplayName,
                                    description = p.Description,
                                    color = p.Color,
                                    required = p.IsRequired,
                                    resourceType = p.ResourceType,
                                    maxConnections = p.MaxConnections,
                                }),
                            resourceOutputPorts = n.GetOutputPorts()
                                .Where(p => p.Kind == PortKind.Resource)
                                .Select(p => new
                                {
                                    name = p.Name,
                                    display = p.DisplayName,
                                    displayName = p.DisplayName,
                                    description = p.Description,
                                    color = p.Color,
                                    required = p.IsRequired,
                                    resourceType = p.ResourceType,
                                    maxConnections = p.MaxConnections,
                                }),
                            credentialsTemplateId = credentialsTemplateId,
                            icon = BuildIconResponse(n, n.NodeType),
                        };
                    });

                return Results.Ok(result);
            }
        )
            .WithSummary("List the available node types with their ports");

        v1.MapGet(
                "/metadata/nodes/{nodeType}/icon.svg",
                async (string nodeType, IEnumerable<Node> nodes, EmbeddedIconService iconService, CancellationToken ct) =>
                {
                    var node = nodes.OfType<BaseNode>().FirstOrDefault(n => n.Name.Equals(nodeType, StringComparison.OrdinalIgnoreCase));
                    if (node?.Icon is null)
                        return ApiNotFound(code: "node_icon_not_found", userMessage: "The requested node icon could not be found.", detail: "Node or icon not found.", context: new { nodeType });

                    if (node.Icon.Type == "svg")
                        return Results.Content(node.Icon.Value, "image/svg+xml");

                    if (!node.Icon.RequiresSvgFetch)
                        return ApiNotFound(code: "node_icon_svg_unavailable", userMessage: "This node icon does not expose SVG content.", detail: "Icon does not expose SVG content.", context: new { nodeType });

                    var svg = await iconService.GetSvgAsync(node.Icon, ct);
                    if (svg is null)
                        return ApiNotFound(code: "node_icon_svg_not_found", userMessage: "The SVG content for this node icon could not be found.", detail: "SVG content not found.", context: new { nodeType });

                    return Results.Content(svg, "image/svg+xml");
                }
            )
            .WithName("GetNodeIconSvg")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml")
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get the SVG icon of a node type");

        v1.MapGet(
            "/metadata/categories",
            () =>
            {
                var categories = NodeCategoryRegistry
                    .All.Values.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        description = c.Description,
                        parentId = c.ParentId,
                        icon = new
                        {
                            type = c.Icon.Type,
                            value = c.Icon.Value,
                            color = c.Icon.Color,
                        },
                    })
                    .OrderBy(c => c.id);

                return Results.Ok(categories);
            }
        )
            .WithSummary("List the node categories");

        return routes;
    }

    internal static object? BuildIconResponse(INodeMetadataProvider node, string nodeType)
    {
        var nodeIcon = (node as BaseNode)?.Icon;
        var legacyIcon = node.GetIconMetadata();

        if (nodeIcon is not null)
        {
            // Resolve color: node-level override first, then category fallback.
            var color = nodeIcon.Color ?? (node.Metadata?.CategoryId is string catId ? NodeCategoryRegistry.Get(catId)?.Icon.Color : null);

            var svgUrl = nodeIcon.RequiresSvgFetch ? $"/api/v1/metadata/nodes/{nodeType}/icon.svg" : null;
            return new
            {
                type = nodeIcon.Type,
                value = nodeIcon.Value,
                color,
                assembly = nodeIcon.AssemblyName,
                svgUrl,
            };
        }

        if (legacyIcon.CodePoint is int codePoint)
        {
            return new
            {
                type = "material",
                value = codePoint.ToString("X"),
                color = legacyIcon.ColorHex,
                assembly = (string?)null,
                svgUrl = (string?)null,
            };
        }

        return null;
    }
}
