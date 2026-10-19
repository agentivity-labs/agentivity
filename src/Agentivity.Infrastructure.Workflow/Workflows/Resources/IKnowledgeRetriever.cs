namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>A knowledge resource that can be searched once for a question, its passages given as text (for a step with no tools, such as an LLM Prompt).</summary>
public interface IKnowledgeRetriever
{
    /// <summary>The passages closest to <paramref name="question"/>, with where they come from, as text to add to a prompt; empty when none.</summary>
    ValueTask<string> RetrieveContextAsync(ResourceBinding binding, string question, int limit, CancellationToken cancellationToken = default);
}
