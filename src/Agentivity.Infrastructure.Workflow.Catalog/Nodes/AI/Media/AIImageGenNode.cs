using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.ImageGeneration;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Services.Media;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Media;

/// <summary>
/// AI Image Generation Node - generates images based on prompts using different modes.
/// Uses IImageGenerationClient for generation and ImageCacheService for storage.
/// </summary>
public sealed class AIImageGenNode : BaseAINode
{
    private readonly IImageGenerationClient _imageClient;
    private readonly ImageCacheService _cacheService;
    private readonly ILogger<AIImageGenNode> _logger;

    public AIImageGenNode(ILlmClientFactory llmClientFactory, IImageGenerationClient imageClient, ImageCacheService cacheService, ILogger<AIImageGenNode> logger)
        : base("ai.media.ai_image_gen", llmClientFactory, logger)
    {
        _imageClient = imageClient ?? throw new ArgumentNullException(nameof(imageClient));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("AI Image Generator", "Generates images using AI based on prompts and config.", "ai.media", whenToUse: "To create an illustration from a description, for an article, a thumbnail or a social media post. Plug an Image Gen Model into it to choose the image provider.");
        DefineEffects(NodeEffects.Costs);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("imagegen_model", "Image Gen Model", ResourcePortCategory.ImageGenModel, "Image generation provider and model configuration");

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Inputs
        DefineInput(
            "simulation",
            ArgumentType.Boolean,
            "Create a placeholder image",
            false,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Execution",
                Order = 10,
            },
            required: false
        );
        DefineInput(
            "instructions",
            ArgumentType.Text,
            "Description of the image to generate",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompt",
                Order = 10,
                Multiline = true,
                Rows = 5,
            },
            required: true
        );
        DefineInput(
            "config",
            ArgumentType.Text,
            "Article config (title, section, etc.)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompt",
                Order = 20,
                Multiline = true,
                Rows = 5,
            },
            required: false
        );
        DefineChoice(
            "size",
            [("square_small", "Square Small"), ("square_medium", "Square Medium"), ("landscape_small", "Landscape Small"), ("landscape_medium", "Landscape Medium"), ("landscape_large", "Landscape Large"), ("portrait_small", "Portrait Small"), ("portrait_medium", "Portrait Medium")],
            "Image size",
            "landscape_medium",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Generation",
                Order = 20,
            },
            required: false
        );
        DefineChoice(
            "mode",
            [("thumbnail", "Thumbnail"), ("article", "Article"), ("pedagogical", "Pedagogical"), ("social", "Social Media")],
            "Generation mode",
            "pedagogical",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Generation",
                Order = 30,
            },
            required: false
        );

        // Outputs
        DefineOutput("image", ArgumentType.ImageRef, "Generated image reference");

        DefineIcon(NodeIcon.Embedded("aiimage"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Starting AI Image Generation...");

        ArgumentInputValues args = GetInputs(config);

        bool simulation = args.GetBoolean("simulation") ?? false;
        string instructions = args.RequireText("instructions");
        string imageContext = args.GetText("config") ?? string.Empty;
        string size = args.GetText("size")?.Trim() ?? "landscape_medium";
        string mode = args.GetText("mode")?.Trim() ?? "pedagogical";
        int count = 1;

        // Simulation mode: return placeholder images without calling the API
        if (simulation)
        {
            services.PublishStatus("Simulation mode enabled - returning placeholder image");
            ImageRef placeholder = GeneratePlaceholderImage(instructions, size, mode);
            return CreateSingleImageResult(placeholder);
        }

        // Get image generation resource
        ResourceBinding binding = ResourceResolver.Require(config, "imagegen_model", ResourcePortCategory.ImageGenModel);
        string credentialsId = binding.RequireString("credentials");
        string? model = binding.GetString("model");
        string? qualityOverride = binding.GetString("quality");
        string? style = binding.GetString("style");

        services.PublishStatus($"Generating {count} image(s) with mode '{mode}', size '{size}'...");

        // Build the formatted prompt based on mode
        string formattedPrompt = mode.Equals("thumbnail", StringComparison.OrdinalIgnoreCase) ? BuildThumbnailPrompt(instructions, imageContext) : BuildPedagogicalPrompt(instructions, imageContext);

        // Determine quality based on mode, resource override, or default
        string quality = qualityOverride ?? (mode.Equals("thumbnail", StringComparison.OrdinalIgnoreCase) ? "medium" : "high");

        // 1. Generate images via the image client
        var request = new ImageGenerationRequest
        {
            Prompt = formattedPrompt,
            CredentialsId = credentialsId,
            Size = size,
            Quality = quality,
            Count = count,
            Model = model,
            Style = style,
        };

        services.PublishStatus($"Calling image generation provider: {_imageClient.ProviderName}...");
        ImageGenerationResult result = await _imageClient.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Image generation failed: {result.Error}");
        }

        /*if (result.Images.Count == 0)
        {
            throw new InvalidOperationException("No images were generated");
        }*/

        services.PublishStatus($"Generated {result.Images.Count} image(s), caching...");

        // 2. Cache each generated image
        var imagesRef = new WorkflowImagesRef();

        for (int i = 0; i < result.Images.Count; i++)
        {
            GeneratedImage generatedImage = result.Images[i];

            ImageRef imageRef = await _cacheService.CacheFromBytesAsync(generatedImage.Data, generatedImage.MimeType, desiredSize: size, cancellationToken).ConfigureAwait(false);

            imagesRef.Add(imageRef);

            services.PublishStatus($"Image {i + 1}/{result.Images.Count} cached: {imageRef.FileName}");
        }

        if (imagesRef.IsEmpty)
        {
            throw new InvalidOperationException("Failed to cache generated image");
        }

        services.PublishStatus("AI Image Generation completed");

        return CreateSingleImageResult(imagesRef.First!);
    }

    private NodeResult CreateSingleImageResult(ImageRef imageRef)
    {
        return NodeResult.Create().AddImageRef("image", imageRef);
    }

    private ImageRef GeneratePlaceholderImage(string instructions, string size, string mode)
    {
        ImageRef imageRef = _cacheService.GetOrCreatePlaceholder(size);
        return imageRef;
    }

    private static string BuildThumbnailPrompt(string instructions, string imageContext)
    {
        return $"""
            Create a professional and modern 2D flat-style illustration for a WordPress blog article thumbnail.

            Topic: "{instructions}"
            config: "{imageContext}"

            Requirements:
            - Do NOT include the article title or blog title
            - Do NOT include any brand logos or human characters
            - The image may contain subtle, config-relevant text such as axis labels, small module titles (e.g. "MACD", "RSI"), or diagram labels
            - Layout must be horizontal (1200x675)

            Design Style:
            - Clean, modern flat design or light 3D dashboard-inspired layout
            - Include symbolic or metaphorical visuals that represent the topic: charts, graphs, molecules, mathematical patterns, etc.
            - You can use floating panels or stylized indicators if relevant
            - Add soft and original background elements (icons, scientific overlays, diagrams, decorative data UI)

            Color Palette:
            - Background: deep navy, neutral grey, or white with contrast
            - Accents: soft blue, teal, orange, green, or lavender (avoid oversaturation)
            - No neon, pastel beige, or harsh primary color blocking

            Goal:
            Produce a beautiful and reusable image without any title, ideal for use as a featured article thumbnail, with config-relevant visual detail and minimal explanatory text embedded in the scene.
            """;
    }

    private static string BuildPedagogicalPrompt(string instructions, string imageContext)
    {
        return $"""
            Create image: {instructions}

            config: {imageContext}

            Note: Do not include any title or header text inside the image.

            ====================
            OBJECTIVE (HIGH PRIORITY)
            ====================
            - Show a realistic use case based on 1 well-defined technical concepts.
            - Every element must serve a clear educational purpose.
            - Do not invent new symbols, shapes, or terminology.
            - Do not stylize or abstract the technical concepts.

            ====================
            CONTEXTUAL CLARITY
            ====================
            - All visual elements (e.g. arrows, labels, highlights) must directly relate to the data or concept shown. They should not be decorative, accurate and relevant.
            - Signals or annotations must be technically justified and placed accordingly.
            - The final image should help a learner understand a specific concept or event.

            ====================
            VISUAL CONVENTIONS
            ====================
            - Respect domain standards:
              - Trading: green = bullish, red = bearish; axes = X time / Y price
              - Electronics: standard resistor/capacitor symbols; correct polarity and flow
              - Anatomy: correct colors and orientation (e.g. arteries = red, veins = blue)
            - Use proportionate, accurate, real-world shapes.
            - Prioritize clarity and recognizability over decoration.

            ====================
            TEXT QUALITY
            ====================
            - Use short, clean, error-free English labels only.
            - Align text properly; no typos or jargon.
            - Valid: "Buy Signal", "EMA 50", "220 Ω", "Mitral Valve"
            - Invalid: "Stgnal", "22O Ohms", "Mitril"

            ====================
            STYLE (NICE TO HAVE)
            ====================
            - Dark dashboard background (#1B2B3C preferred), no transparency
            - Neon-style highlights (electric blue, lime, magenta)
            - Sharp thin lines; 10% padding around edges
            - You can use floating panels or stylized indicators if relevant

            ====================
            FINAL CHECK
            ====================
            - Technically accurate and realistic
            - Logically consistent and educational
            - No decorative, fictitious, or mislabeled elements
            - Fully interpretable by a domain expert
            - Help the user understand the concept, enough detail to be useful
            """;
    }
}
