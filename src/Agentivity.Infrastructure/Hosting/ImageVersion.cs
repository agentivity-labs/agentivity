using System.Text.Json;

namespace Agentivity.Infrastructure.Hosting;

/// <summary>
/// The version of the image the server runs (0.1.23, dev-&lt;commit&gt;-&lt;run&gt;): the <c>version</c> of the <c>edition.json</c> next to its
/// binaries (tools/build/publish-edition.ps1). None for a server run from a checkout.
/// </summary>
public static class ImageVersion
{
    /// <summary>The version of the server running now, or null.</summary>
    public static string? Current { get; } = Read(AppContext.BaseDirectory);

    /// <summary>The version in the <c>edition.json</c> of <paramref name="folder"/>, or null when it has none.</summary>
    public static string? Read(string folder)
    {
        var path = Path.Combine(folder, "edition.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var edition = JsonDocument.Parse(File.ReadAllText(path));
            return edition.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(version.GetString())
                ? version.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
