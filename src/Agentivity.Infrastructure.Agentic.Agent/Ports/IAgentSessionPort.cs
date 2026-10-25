using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Port session complet pour un run agent.
/// Étend <see cref="IRunSessionPort"/> avec les opérations de persistence et compaction.
/// <para>
/// L'implémentation concrète est dans la couche supérieure :
/// - <c>WorkflowAgentSessionPort</c> (Workflow.Agentic) — délègue à <c>AgentSessionCoordinator</c>
/// </para>
/// </summary>
public interface IAgentSessionPort : IRunSessionPort
{
    /// <summary>
    /// Charge les messages de la session précédente.
    /// Retourne une liste vide si pas de session ou <paramref name="restore"/> = false.
    /// </summary>
    ValueTask<IReadOnlyList<LlmChatMessage>> LoadMessagesAsync(bool restore, CancellationToken ct);

    /// <summary>
    /// Persiste l'état final de la session (appel de fin de run), y compris
    /// <c>SessionContext.TranscriptUpToIndex</c> — sans ça, chaque persist écraserait l'index déjà
    /// accumulé. Le résumé roulant lui-même vit sur <c>RunContext.LastTranscriptSummary</c>, pas ici.
    /// </summary>
    ValueTask PersistAsync(IReadOnlyList<LlmChatMessage> messages, int transcriptUpToIndex, CancellationToken ct);

    /// <summary>
    /// Persiste un checkpoint intermédiaire — appelé avant chaque tool call.
    /// Permet la reprise en cas d'interruption (crash, timeout, HIL).
    /// </summary>
    ValueTask CheckpointAsync(IReadOnlyList<LlmChatMessage> messages, int transcriptUpToIndex, CancellationToken ct);

    /// <summary>
    /// Applique la compaction en boucle si la session dépasse le seuil de messages.
    /// Modifie <paramref name="messages"/> en place.
    /// </summary>
    void ApplyCompaction(List<LlmChatMessage> messages);
}
