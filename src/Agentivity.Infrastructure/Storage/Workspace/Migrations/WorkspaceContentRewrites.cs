using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Storage.Workspace.Migrations;

/// <summary>
/// The changes of what the objects hold, in the order they were made; each engine lists them as migrations under the same number
/// (<see cref="WorkspaceContentMigration"/>).
/// </summary>
public static partial class WorkspaceContentRewrites
{
    /// <summary>
    /// 2026-10-18: steps renamed, the same steps under a new identifier — the session kept in memory, and the old "memories", now
    /// Vector Stores. What was removed then (the "AI Knowledge" steps, OpenAI Memory, Web Fetch, the LLM Memory port) has no new
    /// name: an object that used it is left as it is, to be redone.
    /// </summary>
    public const string StepRenames20261018Name = "Steps renamed on 2026-10-18 (sessions in memory, Vector Stores)";

    public static IReadOnlyList<string> StepRenames20261018Holds { get; } = ["ai.sessions.memory", "ai.memory."];

    public static string? StepRenames20261018(string content)
    {
        var renamed = SessionInMemory().Replace(content, "ai.sessions.inmemory");
        renamed = SimpleMemory().Replace(renamed, "ai.vectorstore.inmemory");
        renamed = OtherMemories().Replace(renamed, "ai.vectorstore.$1");
        return renamed;
    }

    [GeneratedRegex(@"ai\.sessions\.memory\b")]
    private static partial Regex SessionInMemory();

    [GeneratedRegex(@"ai\.memory\.simple\b")]
    private static partial Regex SimpleMemory();

    [GeneratedRegex(@"ai\.memory\.(qdrant|pinecone|pgvector)\b")]
    private static partial Regex OtherMemories();
}
