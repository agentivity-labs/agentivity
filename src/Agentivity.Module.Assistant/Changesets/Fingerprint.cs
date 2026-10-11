using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Agentivity.Module.Assistant.Changesets;

/// <summary>
/// A short fingerprint of an entity's content, to notice that the saved version changed while a draft was being worked on.
/// Volatile fields are left out so that merely re-saving an entity does not look like a change.
/// </summary>
public static class Fingerprint
{
    private static readonly string[] Volatile = ["updatedAt", "createdAt"];

    public static string Of(JsonObject entity)
    {
        var copy = (JsonObject)entity.DeepClone();
        Strip(copy);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString()))).ToLowerInvariant()[..16];
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in Volatile)
                    obj.Remove(key);
                foreach (var child in obj.Select(p => p.Value).ToList())
                    Strip(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    Strip(child);
                break;
        }
    }
}
