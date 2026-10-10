using System.Text.Json;

namespace Agentivity.Infrastructure.Storage.Workspace;

internal static class JsonWorkspaceStorageFilePersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void EnsureInitialized(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, "{}");
        }
    }

    public static async Task<TDocument> LoadAsync<TDocument>(string filePath, CancellationToken cancellationToken = default)
        where TDocument : new()
    {
        if (!File.Exists(filePath))
        {
            return new TDocument();
        }

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "{}", StringComparison.Ordinal))
        {
            return new TDocument();
        }

        var data = JsonSerializer.Deserialize<TDocument>(json, JsonOptions);
        return data ?? new TDocument();
    }

    public static async Task SaveAsync<TDocument>(string filePath, TDocument data, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var tempFile = filePath + ".tmp";
        await File.WriteAllTextAsync(tempFile, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempFile, filePath, overwrite: true);
    }
}
