namespace Agentivity.Observability.Contracts;

/// <summary>
/// Surface de trace / debug partagée sur toute la chaîne d'exécution.
/// Collecte les événements de trace de manière thread-safe avec une politique
/// de flush configurable pour éviter l'accumulation mémoire sur les longues chaînes.
/// </summary>
public interface IExecutionTrace
{
    /// <summary>
    /// Ajoute un événement de trace.
    /// Implémentation thread-safe — peut être appelée depuis n'importe quel run de la chaîne.
    /// </summary>
    /// <param name="source">Source de l'événement (ex. : nodeId, agentId, topologyPositionId).</param>
    /// <param name="kind">Nature de l'événement (ex. : "react_step", "tool_call", "hil_gate").</param>
    /// <param name="message">Message humain lisible.</param>
    /// <param name="iteration">Itération de l'agent au moment de l'événement (-1 si non applicable).</param>
    void Append(string source, string kind, string message, int iteration = -1);

    /// <summary>
    /// Flush les événements accumulés vers le stockage durable (ex. : NodeContext trace accumulator).
    /// Vide le buffer interne après le flush.
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}
