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

        DefineMetadata("Image Gen Model", "Exposes an image generation provider credential and model configuration.", "ai.models");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Image Gen Model", ResourcePortCategory.ImageGenModel, maxConnections: -1);

        DefineCredentialsInput("credentials", "openai_image", "Image generation provider credentials (includes base_url)", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput("model", ArgumentType.Text, "Model identifier to use", "dall-e-3", ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 });
        DefineChoice("quality", [("standard", "Standard"), ("hd", "High Definition")], "Image quality", "standard", ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 });
        DefineChoice("style", [("vivid", "Vivid"), ("natural", "Natural")], "Image style", "vivid", ui: new ArgumentUiMetadata { Group = "Settings", Order = 40 });

        DefineIcon(NodeIcon.Embedded("aimodel"));
    }
}
