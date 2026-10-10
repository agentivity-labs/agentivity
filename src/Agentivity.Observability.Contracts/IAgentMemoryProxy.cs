namespace Agentivity.Observability.Contracts;

/// <summary>
/// Port mémoire pour un run agent — déclenche le retrieval RAG avant construction du prompt.
/// Null dans <c>RunContext.Memory</c> = pas de retrieval.
/// <para>
/// Câblé par le chemin workflow via <c>WorkflowAgentMemoryProxy</c> si <c>llm_memory</c> est connecté,
/// et par le chemin standalone via <c>StandaloneAgentMemoryPort</c> si des knowledge sources
/// sont déclarées sur l'entité agent.
/// </para>
/// </summary>
public interface IAgentMemoryProxy
{
    /// <summary>
    /// Retrieve a textual memory context for the given <paramref name="query"/>.
    /// Returns an empty string when no knowledge is available or retrieval fails.
    /// Never throws.
    /// </summary>
    ValueTask<string> RetrieveAsync(string query, int topK, CancellationToken ct);
}
