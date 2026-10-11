using Agentivity.Domain.Workflow;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content;

/// <summary>
/// Link-first content generator that produces DSL-based content from diverse sources.
/// Uses LLM to generate content sections with mandatory link integration, then DocumentLayoutBuilder for deterministic DSL.
///
/// LINK-FIRST APPROACH: ALL provided links MUST be incorporated naturally in the content.
///
/// Flow:
/// 1. LLM: Normalize sources → NormalizedSources (unified link handling)
/// 2. LLM: Generate content sections → List&lt;ContentSection&gt; (with mandatory link integration)
/// 3. Deterministic: DocumentLayoutBuilder → PageNode DSL
/// </summary>
public sealed class ContentGeneratorNode : BaseAINode
{
    private static readonly Regex ListItemPrefixRegex = new(@"^(?:[-*•]\s+|\d+\.\s+)", RegexOptions.Compiled);

    private const string NormalizedSourcesSchemaJson = """
{
  "type": "object",
  "properties": {
    "content_intent": {
      "type": "string",
      "description": "Overall purpose and goal of the content"
    },
    "topics": {
      "type": "array",
      "items": {"type": "string"},
      "description": "Main topics and themes to cover"
    },
    "keywords": {
      "type": "array",
      "items": {"type": "string"},
      "description": "SEO keywords and important terms"
    },
    "links": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "url": {"type": "string"},
          "anchor_text": {"type": "string"},
          "config": {"type": "string"},
          "link_type": {"type": "string", "enum": ["internal", "external", "reference"]}
        }
      },
      "description": "All links (internal/external/references) with config - MUST ALL be used"
    },
    "structure_suggestions": {
      "type": "array",
      "items": {"type": "string"},
      "description": "Suggested sections, headings, or content flow"
    },
    "constraints": {
      "type": "array",
      "items": {"type": "string"},
      "description": "Strict rules that must be followed"
    },
    "tone_guidance": {
      "type": "string",
      "description": "Writing tone and style recommendations"
    },
    "target_audience": {
      "type": "string",
      "description": "Who the content is for"
    }
  },
  "required": ["content_intent", "topics"]
}
""";

    private const string ContentSectionsSchemaJson = """
{
  "type": "object",
  "properties": {
    "sections": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "id": {"type": "string", "description": "Short unique ID (max 20 chars, snake_case)"},
          "title": {"type": "string", "description": "Section heading text"},
          "level": {"type": "integer", "description": "Heading level 1-6"},
          "content": {"type": "string", "description": "Main paragraph content (can include markdown)"},
          "bullet_points": {"type": "array", "items": {"type": "string"}, "description": "List items"},
          "ordered": {"type": "boolean", "description": "True for numbered list"},
          "image_refs": {"type": "array", "items": {"type": "string"}, "description": "Image placeholders like [image:description]"},
          "children": {
            "type": "array",
            "description": "Nested subsections (one level deep)",
            "items": {
              "type": "object",
              "properties": {
                "id": {"type": "string"},
                "title": {"type": "string"},
                "level": {"type": "integer"},
                "content": {"type": "string"},
                "bullet_points": {"type": "array", "items": {"type": "string"}},
                "ordered": {"type": "boolean"},
                "image_refs": {"type": "array", "items": {"type": "string"}}
              },
              "required": ["title", "level"]
            }
          }
        },
        "required": ["title", "level"]
      }
    }
  },
  "required": ["sections"]
}
""";

    private static readonly JsonSerializerOptions SerializerOptions = JsonDefaults.Web;

    private readonly ILogger<ContentGeneratorNode> _logger;

    public ContentGeneratorNode(ILlmClientFactory llmClientFactory, ILogger<ContentGeneratorNode> logger)
        : base("content.content_generator", llmClientFactory, logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Content Generator", "Generates structured DSL content from diverse sources with LLM-powered content generation and deterministic layout.", "content");
        DefineEffects(NodeEffects.Costs);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("llm_model", "LLM Model", ResourcePortCategory.LlmModel, "LLM provider and model configuration");

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // === MAIN CONTENT DESCRIPTION (required) ===
        DefineInput(
            "content_description",
            ArgumentType.Text,
            "Description of the content to generate (user prompt - what, why, for whom)",
            ui: new ArgumentUiMetadata
            {
                Tab = "Content",
                Group = "Brief",
                Order = 10,
                Multiline = true,
                Rows = 8,
            },
            required: true
        );

        // === DATA SOURCES (all optional, any format) ===
        DefineInput(
            "seo_strategy",
            ArgumentType.Text,
            "SEO strategy data (from SEO Advisor or manual)",
            ui: new ArgumentUiMetadata
            {
                Tab = "Sources",
                Group = "SEO",
                Order = 20,
                Multiline = true,
                Rows = 10,
                Format = "json",
            },
            required: false
        );

        DefineInput(
            "links",
            ArgumentType.Text,
            "URLs to include (internal/external/references) - MANDATORY integration with link-first approach",
            ui: new ArgumentUiMetadata
            {
                Tab = "Sources",
                Group = "References",
                Order = 30,
                Multiline = true,
                Rows = 8,
            },
            required: false
        );

        DefineInput(
            "content_brief",
            ArgumentType.Text,
            "Detailed content brief with specific requirements",
            ui: new ArgumentUiMetadata
            {
                Tab = "Sources",
                Group = "Brief",
                Order = 40,
                Multiline = true,
                Rows = 8,
            },
            required: false
        );

        // === CONTENT PARAMETERS ===
        DefineInput(
            "tone",
            ArgumentType.Text,
            "Writing tone: professional, casual, educational, conversational, etc.",
            "professional",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Writing",
                Order = 50,
            },
            required: false
        );

        DefineInput(
            "word_count",
            ArgumentType.Number,
            "Target word count (approximate)",
            1000,
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Writing",
                Order = 60,
                Min = 100,
                Step = 50,
            },
            required: false
        );

        DefineInput(
            "structure_hints",
            ArgumentType.TextList,
            "Structure guidance: sections to include, flow suggestions",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Structure",
                Order = 70,
            },
            required: false
        );

        // === OUTPUT ===
        DefineOutput("dsl_content", ArgumentType.Text, "Generated content in DSL format (PageNode JSON)");

        DefineOutput("metadata", ArgumentType.Text, "Content metadata: topics, keywords, estimated reading time");

        DefineIcon(NodeIcon.Embedded("content"));
    }

    private async ValueTask<string> CompleteAsync(NodeConfig config, WorkflowRunServices services, string prompt, LlmRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required", nameof(prompt));
        }

        (ILlmProvider? llm, ResourceBinding? binding) = ResourceResolver.RequireToolNode<ILlmProvider>(config, "llm_model", ResourcePortCategory.LlmModel);
        OperationResult<LlmResponse> result = await llm.CompleteAsync(binding, prompt, options, cancellationToken).ConfigureAwait(false);

        return result.IsFailure || string.IsNullOrWhiteSpace(result.Value?.Content) ? throw new InvalidOperationException(result.Error ?? "LLM completion failed.") : result.Value.Content;
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        services.PublishStatus("Gathering content generation inputs...");
        ArgumentInputValues args = GetInputs(config);

        // === COLLECT INPUTS ===
        string contentDescription = args.RequireText("content_description");

        // These can be string or JSON objects - serialize if needed
        string? seoStrategy = GetStringOrJson(args, "seo_strategy");
        string? links = GetStringOrJson(args, "links");
        string? contentBrief = GetStringOrJson(args, "content_brief");

        string tone = args.GetText("tone") ?? "professional";
        int wordCount = args.GetInt("word_count") ?? 0;
        if (wordCount <= 0)
            wordCount = 1000;

        IReadOnlyList<string> structureHints = args.GetStringList("structure_hints", []);

        // === STEP 1: NORMALIZE AND AGGREGATE SOURCES (LLM) ===
        services.PublishStatus("Step 1/3: Normalizing content sources with LINK-FIRST approach...");
        NormalizedSources normalizedSources = await NormalizeSourcesAsync(config, services, contentDescription, seoStrategy, links, contentBrief, tone, cancellationToken);

        // === STEP 2: GENERATE CONTENT SECTIONS (LLM) ===
        services.PublishStatus("Step 2/3: Generating content sections with LLM...");
        List<ContentSection> contentSections = await GenerateContentSectionsAsync(config, services, normalizedSources, wordCount, tone, structureHints, cancellationToken);

        // === STEP 3: BUILD DSL LAYOUT (DETERMINISTIC) ===
        services.PublishStatus("Step 3/3: Building DSL layout (deterministic)...");
        PageNode dsl = DocumentLayoutBuilder.Build(
            new DocumentLayoutOptions
            {
                Sections = contentSections.Cast<IContentSection>().ToList(),
                IncludeToc = false, // Removed from inputs - TOC handled by layout logic
                ContainerVariant = "default",
                HeadingVariant = "default",
                TextVariant = "default",
            }
        );

        // === RETURN AS JSON ===
        string metadata = ExtractMetadata(normalizedSources, dsl);

        services.PublishStatus("Content generation completed successfully with LINK-FIRST approach.");

        return NodeResult.Create().AddText("dsl_content", DslSerializer.Serialize(dsl)).AddText("metadata", JsonSerializer.Serialize(metadata));
    }

    private async ValueTask<NormalizedSources> NormalizeSourcesAsync(NodeConfig config, WorkflowRunServices services, string contentDescription, string? seoStrategy, string? links, string? contentBrief, string tone, CancellationToken cancellationToken)
    {
        string systemPrompt = """
            You are a Content Sources Normalizer with LINK-FIRST approach. Your role is to extract and structure information from diverse, unstructured inputs into a clean, deterministic JSON format.

            LINK-FIRST APPROACH (MANDATORY):
            - ALL provided links MUST be incorporated in the final content
            - Links are the PRIMARY driver of content structure and flow
            - Adapt content planning to naturally include every single link
            - Categorize links: internal (same domain), external (other domains), reference (citations)
            - NEVER skip, ignore, or lose any provided link
            - If a link seems difficult to integrate, find creative approaches

            INPUT HANDLING:
            - Accept ANY format: plain text, JSON (nested or flat), URLs, bullet points, notes, etc.
            - Extract intent, topics, keywords, ALL links, structure hints, constraints
            - Resolve conflicts by prioritizing: links integration > explicit constraints > SEO data > briefs
            - If a source is empty or null, ignore it gracefully
            - NEVER invent data - only extract what's present

            LINK EXTRACTION & CLASSIFICATION:
            - Extract ALL URLs from any input source
            - Parse link config, anchor text, and surrounding information
            - Classify as: internal, external, or reference
            - Preserve ALL link metadata (URL, anchor text, config, type)

            EXTRACTION TASKS:
            1. Identify content intent (what is the purpose?)
            2. Extract main topics and themes
            3. List SEO keywords and important terms
            4. Extract and classify ALL links (CRITICAL - must not miss any)
            5. Suggest content structure optimized for link integration
            6. Extract strict constraints (must-follow rules)
            7. Determine tone guidance and target audience

            OUTPUT: Clean, structured JSON matching the schema exactly.
            """;

        var promptBuilder = new StringBuilder();
        _ = promptBuilder.AppendLine("Normalize and aggregate the following content sources with LINK-FIRST approach:");
        _ = promptBuilder.AppendLine();
        _ = promptBuilder.AppendLine("=== PRIMARY DESCRIPTION (USER PROMPT) ===");
        _ = promptBuilder.AppendLine(contentDescription);
        _ = promptBuilder.AppendLine();

        if (!string.IsNullOrWhiteSpace(seoStrategy))
        {
            _ = promptBuilder.AppendLine("=== SEO STRATEGY ===");
            _ = promptBuilder.AppendLine(seoStrategy);
            _ = promptBuilder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(links))
        {
            _ = promptBuilder.AppendLine("=== LINKS (MANDATORY INTEGRATION) ===");
            _ = promptBuilder.AppendLine(links);
            _ = promptBuilder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(contentBrief))
        {
            _ = promptBuilder.AppendLine("=== CONTENT BRIEF ===");
            _ = promptBuilder.AppendLine(contentBrief);
            _ = promptBuilder.AppendLine();
        }

        _ = promptBuilder.AppendLine($"=== TONE PREFERENCE ===");
        _ = promptBuilder.AppendLine(tone);
        _ = promptBuilder.AppendLine();
        _ = promptBuilder.AppendLine("Extract and normalize all information. CRITICAL: Identify and classify ALL links for mandatory integration.");

        string response = await CompleteAsync(config, services, promptBuilder.ToString(), new LlmRequestOptions(Temperature: 0.1, MaxTokens: 2500, SystemPrompt: systemPrompt, ResponseSchema: NormalizedSourcesSchemaJson), cancellationToken);

        string jsonPayload = ExtractJsonPayload(response);
        return JsonSerializer.Deserialize<NormalizedSources>(jsonPayload, SerializerOptions) ?? throw new InvalidOperationException("Failed to deserialize normalized sources");
    }

    private async ValueTask<List<ContentSection>> GenerateContentSectionsAsync(NodeConfig config, WorkflowRunServices services, NormalizedSources sources, int wordCount, string tone, IReadOnlyList<string>? structureHints, CancellationToken cancellationToken)
    {
        string systemPrompt = $"""
            You are a Content Writer with LINK-FIRST MANDATE. Generate structured content sections that MUST incorporate ALL provided links.

            LINK-FIRST APPROACH (CRITICAL):
            - EVERY SINGLE link in SOURCES.links MUST be incorporated in the content
            - Adapt content structure, flow, and narrative to naturally include all links
            - Use creative integration: contextual mentions, comparisons, resource sections, inline references
            - Links should feel natural and valuable, not forced
            - If a link is difficult to integrate, reshape the content to accommodate it
            - FAILURE to include ANY link is unacceptable

            WRITING RULES:
            - Tone: {tone}
            - Target word count: ~{wordCount} words total (distribute across sections)
            - Write complete, engaging paragraphs (not placeholders or outlines)
            - Use markdown in content: **bold**, *italic*, [link text](url)
            - Each section should have a clear purpose and flow

            LINK INTEGRATION STRATEGIES:
            - Inline references: "As explained in [this comprehensive guide](url)..."
            - Comparison mentions: "Unlike [alternative approach](url), this method..."
            - Resource sections: "For additional insights, see [resource name](url)"
            - Contextual citations: "Research from [source](url) shows that..."
            - Follow-up suggestions: "To dive deeper, explore [detailed guide](url)"

            SECTION STRUCTURE:
            - id: Short unique identifier (max 20 chars, snake_case)
            - title: Clear, descriptive heading
            - level: Heading level (2 for main sections, 3 for subsections)
            - content: Main paragraph(s) with complete sentences and LINKS
            - bullet_points: Optional list of key points (can include links) as plain item text only
            - NEVER prefix bullet_points items with list markers like "* ", "- ", "• ", or "1. " because the list structure is already provided separately
            - children: Optional nested subsections

            VALIDATION:
            - Before finalizing, verify ALL links from SOURCES.links are included
            - Ensure natural integration - avoid "link dumping"
            - bullet_points items must be clean text, not pre-formatted markdown or HTML list items
            - Maintain content quality while prioritizing link integration

            OUTPUT: JSON with a "sections" array containing content sections with ALL links integrated.
            """;

        string sourcesJson = JsonSerializer.Serialize(sources, SerializerOptions);

        var promptBuilder = new StringBuilder();
        _ = promptBuilder.AppendLine("Generate content sections based on these normalized sources:");
        _ = promptBuilder.AppendLine();
        _ = promptBuilder.AppendLine("SOURCES:");
        _ = promptBuilder.AppendLine(sourcesJson);
        _ = promptBuilder.AppendLine();

        if (structureHints != null && structureHints.Count > 0)
        {
            _ = promptBuilder.AppendLine("STRUCTURE HINTS (follow this flow):");
            foreach (string hint in structureHints)
            {
                _ = promptBuilder.AppendLine($"- {hint}");
            }
            _ = promptBuilder.AppendLine();
        }

        _ = promptBuilder.AppendLine("Generate complete content sections with real text (not placeholders).");

        string response = await CompleteAsync(config, services, promptBuilder.ToString(), new LlmRequestOptions(Temperature: 0.3, MaxTokens: 4000, SystemPrompt: systemPrompt, ResponseSchema: ContentSectionsSchemaJson), cancellationToken);

        string jsonPayload = ExtractJsonPayload(response);
        GeneratedContent generatedContent = JsonSerializer.Deserialize<GeneratedContent>(jsonPayload, SerializerOptions) ?? throw new InvalidOperationException("Failed to deserialize content sections");

        if (generatedContent.Sections.Count == 0)
        {
            throw new InvalidOperationException("LLM generated no content sections");
        }

        List<ContentSection> normalizedSections = NormalizeGeneratedSections(generatedContent.Sections);
        List<ContentSection> finalizedSections = EnsureAllLinksUsed(normalizedSections, sources.Links);

        return finalizedSections;
    }

    private static List<ContentSection> NormalizeGeneratedSections(List<ContentSection> sections)
    {
        var normalized = new List<ContentSection>(sections.Count);

        foreach (ContentSection section in sections)
        {
            normalized.Add(
                new ContentSection
                {
                    Id = section.Id,
                    Title = section.Title,
                    Level = section.Level,
                    Content = section.Content,
                    BulletPointsList = NormalizeBulletPoints(section.BulletPointsList),
                    IsOrderedList = section.IsOrderedList,
                    ImageRefsList = section.ImageRefsList,
                    ChildSections = section.ChildSections is null ? null : NormalizeGeneratedSections(section.ChildSections),
                }
            );
        }

        return normalized;
    }

    private static List<string>? NormalizeBulletPoints(List<string>? bulletPoints)
    {
        return bulletPoints is null || bulletPoints.Count == 0 ? bulletPoints : bulletPoints.Select(NormalizeBulletPoint).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
    }

    private static string NormalizeBulletPoint(string item)
    {
        return string.IsNullOrWhiteSpace(item) ? string.Empty : ListItemPrefixRegex.Replace(item.Trim(), string.Empty).Trim();
    }

    private static List<ContentSection> EnsureAllLinksUsed(List<ContentSection> sections, IReadOnlyList<LinkReference> links)
    {
        if (sections.Count == 0 || links.Count == 0)
        {
            return sections;
        }

        var missingLinks = links.Where(link => !SectionsContainLink(sections, link.Url)).ToList();

        if (missingLinks.Count == 0)
        {
            return sections;
        }

        // Link-first approach: Add missing links creatively
        ContentSection lastSection = sections[^1];
        var contentBuilder = new StringBuilder(lastSection.Content ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(lastSection.Content))
        {
            _ = contentBuilder.AppendLine();
            _ = contentBuilder.AppendLine();
        }

        // Organize missing links by type
        var internalMissing = missingLinks.Where(l => l.LinkType == "internal").ToList();
        var externalMissing = missingLinks.Where(l => l.LinkType == "external").ToList();
        var referenceMissing = missingLinks.Where(l => l.LinkType == "reference").ToList();

        if (internalMissing.Count > 0)
        {
            _ = contentBuilder.AppendLine("**Related Resources:**");
            foreach (LinkReference? link in internalMissing)
            {
                string anchor = string.IsNullOrWhiteSpace(link.AnchorText) ? link.Url : link.AnchorText;
                string contextSuffix = string.IsNullOrWhiteSpace(link.config) ? "" : $" - {link.config}";
                _ = contentBuilder.AppendLine($"- [{anchor}]({link.Url}){contextSuffix}");
            }
            _ = contentBuilder.AppendLine();
        }

        if (externalMissing.Count > 0)
        {
            _ = contentBuilder.AppendLine("**External References:**");
            foreach (LinkReference? link in externalMissing)
            {
                string anchor = string.IsNullOrWhiteSpace(link.AnchorText) ? link.Url : link.AnchorText;
                string contextSuffix = string.IsNullOrWhiteSpace(link.config) ? "" : $" - {link.config}";
                _ = contentBuilder.AppendLine($"- [{anchor}]({link.Url}){contextSuffix}");
            }
            _ = contentBuilder.AppendLine();
        }

        if (referenceMissing.Count > 0)
        {
            _ = contentBuilder.AppendLine("**Additional Sources:**");
            foreach (LinkReference? link in referenceMissing)
            {
                string anchor = string.IsNullOrWhiteSpace(link.AnchorText) ? link.Url : link.AnchorText;
                string contextSuffix = string.IsNullOrWhiteSpace(link.config) ? "" : $" - {link.config}";
                _ = contentBuilder.AppendLine($"- [{anchor}]({link.Url}){contextSuffix}");
            }
        }

        var updatedLast = new ContentSection
        {
            Id = lastSection.Id,
            Title = lastSection.Title,
            Level = lastSection.Level,
            Content = contentBuilder.ToString().TrimEnd(),
            BulletPointsList = lastSection.BulletPointsList,
            IsOrderedList = lastSection.IsOrderedList,
            ImageRefsList = lastSection.ImageRefsList,
            ChildSections = lastSection.ChildSections,
        };

        var updatedSections = new List<ContentSection>(sections.Count);
        updatedSections.AddRange(sections.Take(sections.Count - 1));
        updatedSections.Add(updatedLast);
        return updatedSections;
    }

    private static bool SectionsContainLink(IEnumerable<ContentSection> sections, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        foreach (ContentSection section in sections)
        {
            if (SectionContainsLink(section, url))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SectionContainsLink(ContentSection section, string url)
    {
        return (!string.IsNullOrWhiteSpace(section.Content) && section.Content.Contains(url, StringComparison.OrdinalIgnoreCase))
            || (section.BulletPointsList != null && section.BulletPointsList.Any(item => item?.Contains(url, StringComparison.OrdinalIgnoreCase) == true))
            || (section.ChildSections != null && section.ChildSections.Any(child => SectionContainsLink(child, url)));
    }

    private static string ExtractMetadata(NormalizedSources sources, PageNode dsl)
    {
        int wordCount = EstimateWordCount(dsl);
        int readingTime = Math.Max(1, wordCount / 200); // ~200 words per minute

        var metadata = new
        {
            topics = sources.Topics,
            keywords = sources.Keywords,
            estimated_word_count = wordCount,
            estimated_reading_time_minutes = readingTime,
            target_audience = sources.TargetAudience,
            tone = sources.ToneGuidance,
        };

        return JsonSerializer.Serialize(metadata, SerializerOptions);
    }

    private static int EstimateWordCount(DslNode node)
    {
        int count = 0;

        count += node switch
        {
            HeadingNode h => h.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            TextNode t => (t.ContentAsString ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            ListNode l => l.Items.Sum(item => item.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
            PageNode p => p.Children.Sum(EstimateWordCount),
            SectionNode s => s.Children.Sum(EstimateWordCount),
            ContainerNode c => c.Children.Sum(EstimateWordCount),
            BlockNode b => b.Children.Sum(EstimateWordCount),
            _ => 0,
        };

        return count;
    }

    /// <summary>
    /// Gets a string value, serializing JSON objects if needed.
    /// </summary>
    private static string? GetStringOrJson(ArgumentInputValues args, string name)
    {
        object? raw = args.GetJson(name);
        return raw is Dictionary<string, object?> or List<object?> ? JsonSerializer.Serialize(raw) : args.GetText(name);
    }

    private sealed record NormalizedSources(string ContentIntent, List<string> Topics, List<string> Keywords, List<LinkReference> Links, List<string> StructureSuggestions, List<string> Constraints, string ToneGuidance, string TargetAudience);

    private sealed record LinkReference(string Url, string AnchorText, string config, string LinkType);
}
