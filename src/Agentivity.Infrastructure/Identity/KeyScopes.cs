namespace Agentivity.Infrastructure.Identity;

/// <summary>What a key may call.</summary>
public static class KeyScopes
{
    /// <summary>The whole API, as its person.</summary>
    public const string Api = "api";

    /// <summary>The API, to read only: what changes nothing (GET).</summary>
    public const string Read = "read";

    /// <summary>The MCP server only.</summary>
    public const string Mcp = "mcp";

    /// <summary>
    /// Made to be seen, in a web page: it starts one published element, from the allowed sites, and follows the executions it
    /// started. Alone: a key is never both secret and visible.
    /// </summary>
    public const string Publishable = "publishable";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Api, Read, Mcp, Publishable], StringComparer.Ordinal);
}
