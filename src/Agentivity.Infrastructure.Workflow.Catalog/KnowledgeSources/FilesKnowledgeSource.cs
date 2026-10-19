using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.KnowledgeBases.Sources;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

/// <summary>
/// Files on the server: one file, or the files of a folder (plain text, Markdown, HTML, JSON). The person's own files only — never
/// those of the installation (its data, its connections, its settings). For an installation of one's own (community): in the
/// hosted edition, nobody but the platform has files on the server.
/// </summary>
public sealed partial class FilesKnowledgeSource : KnowledgeSourceType
{
    public const string Id = "files";

    public override string TypeId => Id;

    public override IReadOnlyList<string> InstallationSettings => ["path"];

    public override int Icon => 0xf091; // Material: folder_outlined

    public override string Name => "Files";

    public override bool ReadsTheServer => true;

    public override string Description => "Text files on the server (plain text, Markdown, HTML, JSON): one file, or the files of a folder.";

    public override IReadOnlyList<ArgumentDefinition> Settings { get; } =
    [
        Input("path", ArgumentType.Text, "Path of a file, or of a folder whose files are read.", ui: new ArgumentUiMetadata { Group = "Files", Order = 10 }, required: true),
        Input("pattern", ArgumentType.Text, "Which files of the folder are read (for example *.md).", "*", ui: new ArgumentUiMetadata { Group = "Files", Order = 20 }, required: false),
        Input("include_patterns", ArgumentType.Text, "More patterns, separated by commas, semicolons or new lines.", "", ui: new ArgumentUiMetadata { Group = "Files", Order = 25, Multiline = true, Rows = 4 }, required: false),
        Input("recursive", ArgumentType.Boolean, "Also read the files of the sub-folders.", false, ui: new ArgumentUiMetadata { Group = "Files", Order = 30 }, required: false),
        Input("json_selector", ArgumentType.Text, "For JSON files: the part that holds the text (for example items[0].content).", "", ui: new ArgumentUiMetadata { Group = "Files", Order = 40 }, required: false),
    ];

    /// <summary>The names, sizes and dates of the files: it changes when a file is added, removed or modified.</summary>
    public override ValueTask<string?> ChangeStampAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        foreach (var file in Files(settings, out _))
        {
            var info = new FileInfo(file);
            builder.Append(file).Append('|').Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return ValueTask.FromResult<string?>(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))));
    }

    protected override async ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(KnowledgeSourceSettings settings, CancellationToken cancellationToken)
    {
        var jsonSelector = settings.GetString("json_selector");
        var documents = new List<KnowledgeDocument>();
        foreach (var file in Files(settings, out var root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var text = ExtractText(file, raw, jsonSelector);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            documents.Add(
                new KnowledgeDocument
                {
                    Text = text,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = file,
                        ["relative_path"] = Path.GetRelativePath(root, file),
                        ["file_name"] = Path.GetFileName(file),
                        ["extension"] = Path.GetExtension(file),
                    },
                }
            );
        }

        return documents;
    }

    /// <summary>The files to read, in order, and the folder they are read from.</summary>
    private static IReadOnlyList<string> Files(KnowledgeSourceSettings settings, out string root)
    {
        // The person's own files — never those of the installation, which a base could point at.
        var path = InstallationFiles.Current.EnsureOutside(PathResolver.Resolve(settings.RequireString("path")));
        if (File.Exists(path))
        {
            root = Path.GetDirectoryName(path) ?? path;
            return [path];
        }

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"The path '{path}' is neither a file nor a folder.");

        root = path;
        var option = settings.GetBool("recursive") == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var folder = path;
        return
        [
            .. Patterns(settings)
                .SelectMany(pattern => Directory.EnumerateFiles(folder, pattern, option))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // A folder may hold the installation, or a link to it: those files are left out.
                .Where(file => !InstallationFiles.Current.Contains(file))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static IReadOnlyList<string> Patterns(KnowledgeSourceSettings settings)
    {
        var include = settings.GetString("include_patterns");
        var patterns = string.IsNullOrWhiteSpace(include) ? [] : include.Split([",", ";", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return patterns.Count == 0 ? [settings.GetString("pattern") ?? "*"] : patterns;
    }

    private static string ExtractText(string file, string raw, string? jsonSelector) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" or ".htm" => HtmlText(raw),
            ".json" => JsonText(raw, jsonSelector),
            _ => raw,
        };

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Styles();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string HtmlText(string raw)
    {
        var content = Styles().Replace(Scripts().Replace(raw, ""), "");
        content = Spaces().Replace(Tags().Replace(content, " "), " ");
        return System.Net.WebUtility.HtmlDecode(content).Trim();
    }

    private static string JsonText(string raw, string? jsonSelector)
    {
        using var document = JsonDocument.Parse(raw);
        return KnowledgeJsonSupport.ElementToText(KnowledgeJsonSupport.SelectElement(document.RootElement, jsonSelector));
    }
}
