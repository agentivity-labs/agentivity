namespace Agentivity.Observability.Contracts;

/// <summary>
/// Surface de télémétrie OTel partagée sur toute la chaîne d'exécution.
/// Permet d'émettre des événements structurés depuis n'importe quel niveau
/// (workflow, agent, team) sans couplage vers <c>NodeContext</c> ou <c>IRunEventBus</c>.
/// </summary>
public interface IExecutionTelemetry
{
    /// <summary>
    /// Émet un événement de télémétrie structuré.
    /// Fire-and-forget — ne lève jamais d'exception.
    /// </summary>
    /// <param name="eventName">Nom de l'événement (ex. : "agent.started", "tool.called").</param>
    /// <param name="payload">Payload sérialisable associé à l'événement.</param>
    /// <param name="stepId">Identifiant optionnel du pas courant (nodeId, topologyPositionId, toolName).</param>
    /// <param name="ct">Token d'annulation.</param>
    ValueTask EmitAsync(string eventName, object? payload, string? stepId = null, CancellationToken ct = default);
}
