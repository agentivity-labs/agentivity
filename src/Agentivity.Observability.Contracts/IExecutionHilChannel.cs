namespace Agentivity.Observability.Contracts;

/// <summary>
/// Canal HIL (Human-in-the-Loop) partagé sur toute la chaîne d'exécution.
/// Permet à n'importe quel niveau (workflow, agent, team, sous-agent) d'interagir
/// avec le même canal humain — chat, formulaire, approbation.
/// </summary>
public interface IExecutionHilChannel
{
    /// <summary>Indique si ce canal HIL est actif pour l'exécution courante.</summary>
    bool IsEnabled { get; }

    /// <summary>Type de canal (ex. : "chat", "form", "approval").</summary>
    string ChannelType { get; }
}
