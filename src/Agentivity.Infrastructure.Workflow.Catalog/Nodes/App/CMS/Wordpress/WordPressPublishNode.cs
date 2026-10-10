using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.WordPress;
using Agentivity.Infrastructure.Workflow.Catalog.Services.App.CMS.Wordpress;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.App.CMS.Wordpress;

public sealed class WordPressPublishNode : ToolNode
{
    private readonly WordPressService _wordpressService;
    private readonly ILogger<WordPressPublishNode> _logger;

    public WordPressPublishNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<WordPressPublishNode> logger)
        : base("wordpress.publish")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(credentialsService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _wordpressService = new WordPressService(httpClientFactory, credentialsService, logger);

        DefineMetadata("Publish to WordPress", "Publishes content as a WordPress post with optional Elementor layout.", "app.cms.wordpress");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        // Define WordPress credentials input
        DefineCredentialsInput(
            "credentials",
            "wordpress",
            "WordPress site credentials",
            ui: new ArgumentUiMetadata
            {
                Tab = "Settings",
                Group = "Connection",
                Order = 10,
            }
        );

        DefineInput(
            "title",
            ArgumentType.Text,
            "Post title",
            ui: new ArgumentUiMetadata
            {
                Tab = "Content",
                Group = "Post",
                Order = 10,
            }
        );
        DefineInput(
            "content",
            ArgumentType.Text,
            "HTML content (optional if elementorData provided)",
            ui: new ArgumentUiMetadata
            {
                Tab = "Content",
                Group = "Body",
                Order = 20,
                Multiline = true,
                Rows = 10,
            }
        );
        DefineInput(
            "elementorData",
            ArgumentType.Text,
            "Elementor layout data",
            ui: new ArgumentUiMetadata
            {
                Tab = "Content",
                Group = "Body",
                Order = 30,
                Multiline = true,
                Rows = 10,
                Format = "json",
            }
        );
        DefineInput(
            "metaTags",
            ArgumentType.Text,
            "Meta tags / keywords",
            ui: new ArgumentUiMetadata
            {
                Tab = "SEO",
                Group = "Metadata",
                Order = 40,
            }
        );
        DefineInput(
            "metaDescription",
            ArgumentType.Text,
            "Meta description",
            ui: new ArgumentUiMetadata
            {
                Tab = "SEO",
                Group = "Metadata",
                Order = 50,
                Multiline = true,
                Rows = 3,
            }
        );
        DefineInput(
            "slug",
            ArgumentType.Text,
            "Post slug (auto-generated from title if not provided)",
            ui: new ArgumentUiMetadata
            {
                Tab = "SEO",
                Group = "Metadata",
                Order = 60,
            }
        );
        DefineInput(
            "categories",
            ArgumentType.Text,
            "Categories",
            ui: new ArgumentUiMetadata
            {
                Tab = "Publishing",
                Group = "Taxonomy",
                Order = 70,
            }
        );
        DefineInput(
            "status",
            ArgumentType.Text,
            "Post status",
            "draft",
            ui: new ArgumentUiMetadata
            {
                Tab = "Publishing",
                Group = "Publishing",
                Order = 80,
            }
        );
        DefineInput(
            "featuredMediaId",
            ArgumentType.Number,
            "Featured image media id",
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Featured Image",
                Order = 90,
            }
        );
        DefineInput(
            "updateFeaturedImage",
            ArgumentType.Boolean,
            "Whether to update featured image",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Media",
                Group = "Featured Image",
                Order = 100,
            }
        );

        DefineOutput("result", ArgumentType.Text, "Result of the WordPress post publication");

        DefineIcon(NodeIcon.Embedded("wordpress"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string credentialsId = args.RequireText("credentials");

        string? title = config.Resolve("title")?.ToString();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"Workflow node '{config.Node.Id}' requires 'title'.");
        }
        title = title.Trim();

        string metaDescription = config.Resolve("metaDescription")?.ToString()?.Trim() ?? string.Empty;
        string status = config.Resolve("status")?.ToString()?.Trim() ?? "draft";
        string content = config.Resolve("content")?.ToString()?.Trim() ?? string.Empty;
        JsonNode? elementorData = ResolveElementor(config, services);

        // Either content or elementorData must be provided
        if (string.IsNullOrWhiteSpace(content) && elementorData is null)
        {
            throw new InvalidOperationException($"Workflow node '{config.Node.Id}' requires either 'content' or 'elementorData'.");
        }

        IReadOnlyList<string> metaTags = ResolveStringList(config, services, "metaTags", required: false);
        IReadOnlyList<int> categories = ResolveIntList(config, services, "categories");

        // Auto-generate slug from title if not provided
        string? slug = config.Resolve("slug")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = GenerateSlug(title);
        }

        int? featuredMedia = ResolveInt(config, services, "featuredMediaId");
        bool updateFeatured = ResolveBool(config, services, "updateFeaturedImage");

        var request = new WordPressPostRequest(title, metaTags, metaDescription, status, content, elementorData, categories, featuredMedia, slug, updateFeatured);

        OperationResult<WordPressPostResult> result = await _wordpressService.PublishPostAsync(credentialsId, request, cancellationToken).ConfigureAwait(false);
        return result.IsFailure || result.Value is null
            ? throw new InvalidOperationException(result.Error ?? "WordPress publish failed.")
            : NodeResult
                .Create()
                .AddText(
                    "result",
                    JsonSerializer.Serialize(
                        new
                        {
                            postId = result.Value.PostId,
                            permalink = result.Value.Permalink,
                            featuredMediaUpdated = result.Value.FeaturedMediaUpdated,
                        }
                    )
                );
    }

    private static string GenerateSlug(string title)
    {
        // Simple slug generation: lowercase, replace spaces with hyphens, remove special chars
        string slug = title.ToLowerInvariant().Replace(" ", "-").Replace("'", "").Replace("\"", "");

        // Remove consecutive hyphens
        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug.Trim('-');
    }

    private static JsonNode? ResolveElementor(NodeConfig config, WorkflowRunServices services)
    {
        object? raw = config.Resolve("elementorData");
        if (raw is null)
            return null;

        if (raw is string textValue && !string.IsNullOrWhiteSpace(textValue))
        {
            try
            {
                return JsonNode.Parse(textValue);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON in elementorData: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException($"Invalid elementorData: must be a JSON string");
    }

    private static IReadOnlyList<string> ResolveStringList(NodeConfig config, WorkflowRunServices services, string parameterName, bool required = true)
    {
        object? resolved = config.Resolve(parameterName);
        if (resolved is null)
        {
            return required ? throw new InvalidOperationException($"wordpress.publish parameter '{parameterName}' is required.") : Array.Empty<string>();
        }

        var values = new List<string>();

        if (resolved is IReadOnlyList<string> listValue)
        {
            values.AddRange(listValue.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        else if (resolved is string textValue && !string.IsNullOrWhiteSpace(textValue))
        {
            // Try parse as JSON array
            if (textValue.TrimStart().StartsWith("["))
            {
                try
                {
                    string[]? arr = JsonSerializer.Deserialize<string[]>(textValue);
                    if (arr is not null)
                        values.AddRange(arr.Where(v => !string.IsNullOrWhiteSpace(v)));
                }
                catch
                {
                    values.Add(textValue.Trim());
                }
            }
            else
            {
                values.Add(textValue.Trim());
            }
        }

        return values.Count == 0 && required ? throw new InvalidOperationException($"wordpress.publish parameter '{parameterName}' is empty.") : (IReadOnlyList<string>)values;
    }

    private static IReadOnlyList<int> ResolveIntList(NodeConfig config, WorkflowRunServices services, string parameterName)
    {
        object? resolved = config.Resolve(parameterName);
        if (resolved is null)
        {
            return Array.Empty<int>();
        }

        var values = new List<int>();

        if (resolved is string textValue && !string.IsNullOrWhiteSpace(textValue))
        {
            // Try parse as JSON array
            if (textValue.TrimStart().StartsWith("["))
            {
                try
                {
                    int[]? arr = JsonSerializer.Deserialize<int[]>(textValue);
                    if (arr is not null)
                        values.AddRange(arr);
                }
                catch
                {
                    if (int.TryParse(textValue, out int parsed))
                        values.Add(parsed);
                }
            }
            else if (int.TryParse(textValue, out int parsed))
            {
                values.Add(parsed);
            }
        }
        else if (resolved is double numValue)
        {
            values.Add((int)numValue);
        }

        return values;
    }

    private static int? ResolveInt(NodeConfig config, WorkflowRunServices services, string parameterName)
    {
        object? resolved = config.Resolve(parameterName);
        return resolved is null ? null
            : resolved is double numValue ? (int)numValue
            : resolved is string textValue && int.TryParse(textValue, out int parsed) ? parsed
            : null;
    }

    private static bool ResolveBool(NodeConfig config, WorkflowRunServices services, string parameterName)
    {
        object? resolved = config.Resolve(parameterName);
        return resolved is not null && (resolved is bool boolValue ? boolValue : resolved is string textValue && bool.TryParse(textValue, out bool parsed) && parsed);
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        result = 0;
        return value switch
        {
            int i => (result = i) == i,
            long l when l is >= int.MinValue and <= int.MaxValue => (result = (int)l) == (int)l,
            double d when Math.Abs(d % 1) < double.Epsilon => (result = (int)d) == (int)d,
            string s when int.TryParse(s, out result) => true,
            _ => false,
        };
    }
}
