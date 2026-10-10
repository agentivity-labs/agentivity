using System.Text;
using Agentivity.Infrastructure.Workflows.Nodes;
using Node = Agentivity.Infrastructure.Workflows.Node;

namespace Agentivity.Infrastructure.Workflow.Catalog.AI.Assistant;

/// <summary>
/// Builds the knowledge base context for the Agentivity AI Assistant.
/// Contains all information about the platform: nodes, DSL, workflows, etc.
/// </summary>
public sealed class AssistantKnowledgeBase
{
    private readonly IEnumerable<Node> _registeredNodes;
    private string? _cachedContext;

    public AssistantKnowledgeBase(IEnumerable<Node> registeredNodes)
    {
        _registeredNodes = registeredNodes ?? throw new ArgumentNullException(nameof(registeredNodes));
    }

    /// <summary>
    /// Gets the complete system prompt with all platform knowledge.
    /// </summary>
    public string GetSystemPrompt()
    {
        string templateExample = "{{ node_id.output_name }}";
        string nodesContext = GetNodesContext();

        return $"""
            You are the Agentivity AI Assistant, an expert on the Agentivity workflow automation platform.

            # YOUR ROLE
            You help users:
            - Design and create workflows
            - Understand which nodes to use for specific tasks
            - Fill in node configurations correctly
            - Debug workflow issues
            - Propose improvements and best practices

            # PLATFORM OVERVIEW
            Agentivity is a visual workflow automation platform that:
            - Uses a node-based system where each node performs a specific action
            - Supports AI-powered content generation (articles, images, SEO)
            - Publishes content to WordPress with Elementor layouts
            - Uses a DSL (Domain Specific Language) for layout design

            # NODE SYSTEM
            Each node has:
            - A unique `nodeType` identifier (e.g., "content.articles.article_writer")
            - Inputs: parameters that configure the node
            - Outputs: data produced by the node that can be used by other nodes
            - Connections are made using template syntax: {templateExample}

            # AVAILABLE NODES
            {nodesContext}

            # DSL LAYOUT SYSTEM
            The layout DSL is a JSON structure for defining page layouts:
            - `page` - Root container
            - `container` - Groups content
            - `section` - Content section with optional title
            - `block` - Styled container (variant: default, accent, boxed, etc.)
            - `heading` - Heading text (level: 1-6, variant: default, centered_white, etc.)
            - `text` - Paragraph content
            - `image` - Image reference
            - `button` - Action button
            - `list` - Bullet/numbered list
            - `toc` - Table of contents
            - `spacer` - Vertical spacing
            - `divider` - Visual separator

            # WORKFLOW JSON FORMAT
            Workflows are stored as JSON with nodes and connections.
            Use {templateExample} syntax to reference outputs from other nodes.

            # BEST PRACTICES
            1. Always use meaningful node IDs that describe their purpose
            2. Use the Set node to define workflow variables at the start
            3. Connect outputs to inputs using template syntax
            4. Use foreach loops for batch operations
            5. Store data in dataTables for persistence across nodes

            # RESPONSE GUIDELINES
            - Be concise but thorough
            - Provide JSON examples when helpful
            - Suggest specific nodes for user's needs
            - If a feature doesn't exist, suggest workarounds or new node ideas
            - Always validate that node connections are compatible
            """;
    }

    /// <summary>
    /// Gets the context about all available nodes.
    /// </summary>
    public string GetNodesContext()
    {
        if (_cachedContext is not null)
            return _cachedContext;

        var sb = new StringBuilder();

        IOrderedEnumerable<IGrouping<string, INodeMetadataProvider>> nodesByCategory = _registeredNodes.OfType<INodeMetadataProvider>().GroupBy(n => GetNodeCategory(n.NodeType)).OrderBy(g => g.Key);

        foreach (IGrouping<string, INodeMetadataProvider>? category in nodesByCategory)
        {
            _ = sb.AppendLine($"\n## {category.Key}");

            foreach (INodeMetadataProvider? node in category.OrderBy(n => n.NodeType))
            {
                string description = node.Metadata?.Description ?? "No description";
                _ = sb.AppendLine($"- **{node.NodeType}**: {description}");

                // List inputs
                IReadOnlyList<NodeValueDescriptor> inputs = node.GetInputs();
                if (inputs.Count > 0)
                {
                    _ = sb.AppendLine("  Inputs:");
                    foreach (NodeValueDescriptor input in inputs)
                    {
                        _ = sb.AppendLine($"    - `{input.Name}`: {input.Type}");
                    }
                }

                // List outputs
                IReadOnlyList<NodeValueDescriptor> outputs = node.GetOutputs();
                if (outputs.Count > 0)
                {
                    _ = sb.AppendLine("  Outputs:");
                    foreach (NodeValueDescriptor output in outputs)
                    {
                        _ = sb.AppendLine($"    - `{output.Name}`: {output.Type}");
                    }
                }
            }
        }

        _cachedContext = sb.ToString();
        return _cachedContext;
    }

    private static string GetNodeCategory(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
            return "unknown";

        string[] parts = nodeType.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }

    /// <summary>
    /// Invalidates the cached context, forcing a rebuild on next access.
    /// </summary>
    public void InvalidateCache()
    {
        _cachedContext = null;
    }
}
