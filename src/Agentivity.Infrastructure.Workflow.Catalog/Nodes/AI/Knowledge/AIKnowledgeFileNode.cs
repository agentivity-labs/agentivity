using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Knowledge;

public sealed class AIKnowledgeFileNode : ResourceNode, IKnowledgeSource
{
    public AIKnowledgeFileNode()
        : base("ai.knowledge.file")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.KnowledgeSource);

        DefineMetadata("AI Knowledge (File)", "Loads a knowledge corpus from a file or a directory pattern and exposes normalized documents for AI consumers.", "ai.knowledge");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Knowledge Source", ResourcePortCategory.KnowledgeSource, maxConnections: -1);
        DefineInput("path", ArgumentType.Text, "Absolute or relative file path, or a directory path to scan for knowledge files.", ui: new ArgumentUiMetadata { Group = "Knowledge", Order = 10 });
        DefineInput("pattern", ArgumentType.Text, "Search pattern used when 'path' points to a directory.", "*", ui: new ArgumentUiMetadata { Group = "Knowledge", Order = 20 });
        DefineInput(
            "include_patterns",
            ArgumentType.Text,
            "Optional additional directory patterns separated by commas, semicolons, or new lines.",
            "",
            ui: new ArgumentUiMetadata
            {
                Group = "Knowledge",
                Order = 25,
                Multiline = true,
                Rows = 4,
            }
        );
        DefineInput("recursive", ArgumentType.Boolean, "When enabled, scans subdirectories when 'path' points to a directory.", false, ui: new ArgumentUiMetadata { Group = "Knowledge", Order = 30 });
        DefineInput("json_selector", ArgumentType.Text, "Optional selector used when reading JSON files (for example: items[0].content).", "", ui: new ArgumentUiMetadata { Group = "Knowledge", Order = 40 });

        DefineIcon(NodeIcon.Embedded("file"));
    }

    public async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(binding);

        string path = PathResolver.Resolve(binding.RequireString("path"));
        bool recursive = binding.GetBool("recursive") ?? false;
        string? jsonSelector = binding.GetString("json_selector");

        if (File.Exists(path))
        {
            return [await LoadDocumentAsync(Path.GetDirectoryName(path) ?? path, path, jsonSelector, cancellationToken).ConfigureAwait(false)];
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Knowledge path '{path}' does not exist as a file or directory.");
        }

        IReadOnlyList<string> patterns = ParsePatterns(binding);
        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var documents = new List<KnowledgeDocument>();

        IOrderedEnumerable<string> files = patterns.SelectMany(pattern => Directory.EnumerateFiles(path, pattern, searchOption)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase);

        foreach (string? filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            KnowledgeDocument document = await LoadDocumentAsync(path, filePath, jsonSelector, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(document.Text))
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    private static async Task<KnowledgeDocument> LoadDocumentAsync(string rootPath, string filePath, string? jsonSelector, CancellationToken cancellationToken)
    {
        string raw = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        string extension = Path.GetExtension(filePath);
        string text = ExtractText(filePath, raw, jsonSelector);
        return new KnowledgeDocument
        {
            Text = text,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = filePath,
                ["relative_path"] = Path.GetRelativePath(rootPath, filePath),
                ["file_name"] = Path.GetFileName(filePath),
                ["extension"] = extension,
            },
        };
    }

    private static IReadOnlyList<string> ParsePatterns(ResourceBinding binding)
    {
        string? includePatterns = binding.GetString("include_patterns");
        if (string.IsNullOrWhiteSpace(includePatterns))
        {
            return [binding.GetString("pattern") ?? "*"];
        }

        var patterns = includePatterns.Split([",", ";", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(pattern => !string.IsNullOrWhiteSpace(pattern)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return patterns.Count == 0 ? [binding.GetString("pattern") ?? "*"] : patterns;
    }

    private static string ExtractText(string filePath, string raw, string? jsonSelector)
    {
        string extension = Path.GetExtension(filePath);
        return extension.ToLowerInvariant() switch
        {
            ".html" or ".htm" => ExtractHtmlText(raw),
            ".json" => ExtractJsonText(raw, jsonSelector),
            _ => raw,
        };
    }

    private static string ExtractHtmlText(string raw)
    {
        string content = Regex.Replace(raw, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        content = Regex.Replace(content, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        content = Regex.Replace(content, @"<[^>]+>", " ");
        content = Regex.Replace(content, @"\s+", " ");
        return System.Net.WebUtility.HtmlDecode(content).Trim();
    }

    private static string ExtractJsonText(string raw, string? jsonSelector)
    {
        using var document = JsonDocument.Parse(raw);
        JsonElement selected = KnowledgeJsonSupport.SelectElement(document.RootElement, jsonSelector);
        return KnowledgeJsonSupport.ElementToText(selected);
    }
}
