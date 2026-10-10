namespace Agentivity.Domain.Executable;

/// <summary>
/// Décrit un port d'entrée ou de sortie d'une définition exécutable.
/// Type léger intentionnel — pas de <c>Value</c>, <c>BindingExpression</c> ou <c>Ui</c>
/// (ces propriétés appartiennent à <c>ArgumentDefinition</c> dans le moteur workflow).
/// </summary>
public sealed record PortDefinition(string Name, string Type, string? Description = null);
