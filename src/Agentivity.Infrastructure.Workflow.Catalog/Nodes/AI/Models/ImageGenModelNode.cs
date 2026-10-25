using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

public sealed class ImageGenModelNode : ResourceNode
{
    public ImageGenModelNode()
        : base("imagegen.model")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.ImageGenModel);

        DefineMetadata("Image Gen Model", "Exposes an image generation provider credential and model configuration.", "ai.models", whenToUse: "To choose the image provider, model, quality and style used by the AI Image Generator. Plug it into the AI Image Generator.");
        DefineEffects(NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Image Gen Model", ResourcePortCategory.ImageGenModel, maxConnections: -1);

        DefineCredentialsInput("credentials", "openai_image", "Image generation provider credentials (includes base_url)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("model", ArgumentType.Text, "OpenAI image model to draw with (gpt-image family)", "gpt-image-1.5", ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 }, required: false);
        DefineChoice("quality", [("low", "Low (fast)"), ("medium", "Medium"), ("high", "High")], "Image quality", "medium", ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 }, required: false);
        DefineChoice("style", [("none", "As described"), ("vivid", "Vivid"), ("natural", "Natural")], "Look of the image, said to the model with the description", "none", ui: new ArgumentUiMetadata { Group = "Settings", Order = 40 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimodel"));
    }
}
