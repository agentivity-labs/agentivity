using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// The configurations (the presets of the Elementor layout, …), a domain of the workspace: each one a JSON document kept under its
/// name — a relative path like <c>elementor/presets.json</c> — by the workspace module that is loaded. A name is never a place on
/// the disk (<see cref="NameOf"/>): reading or writing a configuration reaches nothing else, whatever is asked.
/// <para>
/// A configuration is kept as it was written — its text, comments and trailing commas included, which a hand-written file has —
/// and read as before (comments skipped, trailing commas allowed).
/// </para>
/// <para>
/// The files kept before in the configuration folder (<c>Configuration:StoragePath</c>, by default <c>_data/config</c>) are taken
/// over one by one, the first time each is asked for or listed, and left as they were. There is no deletion of a configuration, so
/// one taken over is never taken over again.
/// </para>
/// </summary>
public sealed class ConfigurationRepository : IConfigurationRepository
{
    public const string ConfigurationType = "configuration";

    private static readonly JsonSerializerOptions ReadOptions = JsonDefaults.Configuration;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonDocumentOptions HandWritten = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly IWorkspaceObjectStore _store;
    private readonly string _legacyFolder;
    private readonly ILogger<ConfigurationRepository> _logger;
    private readonly ConcurrentDictionary<string, bool> _checked = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationRepository(IWorkspaceObjectStore store, ConfigurationOptions options, ILogger<ConfigurationRepository> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _legacyFolder = PathResolver.Resolve(string.IsNullOrWhiteSpace(options.StoragePath) ? "_data/config" : options.StoragePath);
        _logger = logger;
    }

    /// <summary>
    /// The name of a configuration, as it is kept: a relative path with <c>/</c> between its parts — never a drive, a leading
    /// <c>/</c>, a <c>.</c> or <c>..</c> part, or a character a file name cannot have. Throws <see cref="ConfigurationPathException"/>.
    /// </summary>
    public static string NameOf(string? path)
    {
        var name = (path ?? string.Empty).Trim().Replace('\\', '/');
        var parts = name.Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || Path.IsPathRooted(name) || parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(invalid) >= 0))
        {
            throw new ConfigurationPathException($"'{path}' is not the name of a configuration: a name is a relative path like elementor/presets.json — no drive, no leading /, no . or .. part.");
        }

        return name;
    }

    /// <summary>The id a configuration is kept under: its name, the same whatever its case, with no <c>/</c> (some workspace modules make a file of it).</summary>
    public static string IdOf(string name) => NameOf(name).ToLowerInvariant().Replace("/", "__");

    public async Task<T?> LoadAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        where T : class
    {
        if (!TryName(relativePath, out var name))
            return null;

        var text = TextOf(await FindAsync(name, cancellationToken).ConfigureAwait(false));
        return text is null ? null : JsonSerializer.Deserialize<T>(text, ReadOptions);
    }

    public T? Load<T>(string relativePath)
        where T : class => LoadAsync<T>(relativePath).GetAwaiter().GetResult();

    public Task SaveAsync<T>(string relativePath, T data, CancellationToken cancellationToken = default)
        where T : class => SaveRawAsync(relativePath, JsonSerializer.Serialize(data, WriteOptions), cancellationToken);

    public async Task SaveRawAsync(string relativePath, string jsonContent, CancellationToken cancellationToken = default)
    {
        var name = NameOf(relativePath);
        using (JsonDocument.Parse(jsonContent)) { } // Not JSON: refused, nothing kept.
        await KeepAsync(name, jsonContent, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        _checked[name] = true;
        _logger.LogInformation("Saved configuration: {Name}", name);
    }

    public IEnumerable<ConfigurationFileInfo> ListFiles(string? subDirectory = null)
    {
        var prefix = string.IsNullOrWhiteSpace(subDirectory) ? null : NameOf(subDirectory) + "/";
        TakeOverEveryFileAsync(CancellationToken.None).GetAwaiter().GetResult();
        return _store
            .ListAsync(ConfigurationType)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            .Select(e => (Envelope: e, Kept: JsonNode.Parse(e.Payload)))
            .Select(x => (x.Envelope, Name: x.Kept?["path"]?.GetValue<string>() ?? x.Envelope.Name, Text: TextOf(x.Kept)))
            .Where(x => prefix is null || x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => new ConfigurationFileInfo(x.Name, Path.GetFileNameWithoutExtension(x.Name), x.Text?.Length ?? 0, x.Envelope.Metadata.UpdatedAtUtc.UtcDateTime))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool Exists(string relativePath) => TryName(relativePath, out var name) && FindAsync(name, CancellationToken.None).GetAwaiter().GetResult() is not null;

    private static bool TryName(string? path, out string name)
    {
        try
        {
            name = NameOf(path);
            return true;
        }
        catch (ConfigurationPathException)
        {
            name = string.Empty;
            return false;
        }
    }

    private async Task<JsonNode?> FindAsync(string name, CancellationToken cancellationToken)
    {
        var kept = await _store.GetAsync(ConfigurationType, IdOf(name), cancellationToken).ConfigureAwait(false);
        if (kept.Success)
            return JsonNode.Parse(kept.Value!.Payload);

        return await TakeOverFileAsync(name, cancellationToken).ConfigureAwait(false) ? JsonNode.Parse((await _store.GetAsync(ConfigurationType, IdOf(name), cancellationToken).ConfigureAwait(false)).Value!.Payload) : null;
    }

    /// <summary>The file of that name in the configuration folder of before, taken over if it is there and not taken over yet.</summary>
    private async Task<bool> TakeOverFileAsync(string name, CancellationToken cancellationToken)
    {
        if (_checked.ContainsKey(name))
            return false;

        var file = Path.Combine(_legacyFolder, name.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            _checked[name] = true;
            return false;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            using (JsonDocument.Parse(text, HandWritten)) { }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The configuration file {File} is not valid JSON: it was not taken over, and was left as it is", file);
            _checked[name] = true;
            return false;
        }

        await KeepAsync(name, text, File.GetLastWriteTimeUtc(file), cancellationToken).ConfigureAwait(false);
        _checked[name] = true;
        _logger.LogInformation("Took over the configuration {Name} from {File}, which was left as it is", name, file);
        return true;
    }

    private async Task TakeOverEveryFileAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_legacyFolder))
            return;

        foreach (var file in Directory.EnumerateFiles(_legacyFolder, "*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(_legacyFolder, file).Replace('\\', '/');
            if (TryName(name, out var valid) && !(await _store.GetAsync(ConfigurationType, IdOf(valid), cancellationToken).ConfigureAwait(false)).Success)
            {
                await TakeOverFileAsync(valid, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string? TextOf(JsonNode? kept) => kept?["text"]?.GetValue<string>();

    private async Task KeepAsync(string name, string text, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["name"] = name, ["path"] = name, ["text"] = text };
        var saved = await _store.SaveAsync(WorkspaceObjectEnvelope.CreateJson(ConfigurationType, IdOf(name), name, payload.ToJsonString(WriteOptions), new WorkspaceObjectMetadata(updatedAt, updatedAt)), cancellationToken).ConfigureAwait(false);
        if (!saved.Success)
            throw new InvalidOperationException($"The configuration {name} could not be kept in the workspace: {saved.Error}");
    }
}

/// <summary>A configuration was asked for by something that is not a configuration's name (a place on the disk).</summary>
public sealed class ConfigurationPathException(string message) : ArgumentException(message), IUserFacingError
{
    public int HttpStatus => 400;
    public string Code => "configuration_path_invalid";
    public string UserMessage => Message;
}

/// <summary>Represents information about a configuration.</summary>
public sealed record ConfigurationFileInfo(string Path, string Name, long Size, DateTime LastModified);
