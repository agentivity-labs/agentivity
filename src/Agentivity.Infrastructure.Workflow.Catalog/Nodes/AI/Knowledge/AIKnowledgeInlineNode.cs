using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeInlineNode : ResourceNode, IKnowledgeSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public AIKnowledgeInlineNode()
        : base("ai.knowledge.inline")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (Inline)", "Provides an inline knowledge corpus as normalized documents for AI consumers.", "ai.knowledge");
        DefineEffects(NodeEffects.None);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput(
            "documents",
            ArgumentType.Text,
            "Knowledge documents as JSON array of {text, metadata}",
            "[]",
            ui: new ArgumentUiMetadata
            {
                Group = "Knowledge",
                Order = 10,
                Multiline = true,
                Rows = 10,
                Format = "json",
            },
            required: false
        );

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string raw = binding.GetString("documents") ?? "[]";
        List<KnowledgeDocument> documents = JsonSerializer.Deserialize<List<KnowledgeDocument>>(raw, SerializerOptions) ?? [];
        return ValueTask.FromResult<IReadOnlyList<KnowledgeDocument>>(documents.Where(document => !string.IsNullOrWhiteSpace(document.Text)).ToList());
    }
}
