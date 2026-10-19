namespace Agentivity.Infrastructure.Core.Llm.Knowledge;

/// <summary>A text read from a source (a file, a page, a row…), with what is known of it: what a Knowledge Base source type reads.</summary>
public sealed record KnowledgeDocument
{
    public string Text { get; init; } = string.Empty;

    public Dictionary<string, string>? Metadata { get; init; }
}
