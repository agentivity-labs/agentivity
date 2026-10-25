namespace Agentivity.Domain.Executable;

/// <summary>
/// Contrat commun aux définitions exécutables créées par l'utilisateur :
/// <c>AgentEntity</c>, <c>TeamEntity</c> et <c>WorkflowEntity</c>.
/// <para>
/// Shared contract for user-defined executable definitions: agents, teams, and workflows.
/// L'interface vit dans <c>Agentivity.Domain</c> pour que les entités puissent
/// l'implémenter sans dépendance circulaire vers l'infrastructure.
/// </para>
/// </summary>
public interface IExecutableDefinition
{
    /// <summary>Identifiant unique dans le catalogue.</summary>
    string Id { get; }

    /// <summary>Nom d'affichage dans l'UI et le designer.</summary>
    string Name { get; }

    /// <summary>Type de la définition : Agent, Team ou Workflow.</summary>
    ExecutableKind Kind { get; }

    /// <summary>Rôle de l'entité — guide le LLM pour choisir le prochain agent dans une team.</summary>
    string? Role { get; }

    /// <summary>Tags libres pour la recherche et le filtrage dans la bibliothèque (voir <see cref="Entity.Tags"/>).</summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>Ports d'entrée déclarés par cette définition.</summary>
    IReadOnlyList<PortDefinition> Inputs { get; }

    /// <summary>Ports de sortie déclarés par cette définition.</summary>
    IReadOnlyList<PortDefinition> Outputs { get; }
}
