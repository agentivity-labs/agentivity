namespace Agentivity.Domain.Executable;

/// <summary>
/// Discriminant du type d'une définition exécutable créée par l'utilisateur.
/// Discriminant for user-defined executable definitions.
/// </summary>
public enum ExecutableKind
{
    Agent,
    Team,
    Workflow,
}
