namespace Agentivity.Observability.Contracts;

/// <summary>
/// Port session propre à un run.
/// Abstraction minimale — sans types LLM-spécifiques pour rester dans la couche contracts.
/// Les implémentations concrètes (workflow, standalone) sont dans les couches supérieures.
/// </summary>
public interface IRunSessionPort
{
    /// <summary>Identifiant de la session chargée. Null si pas de session active.</summary>
    string? SessionId { get; }

    /// <summary>Vrai si une session est active pour ce run.</summary>
    bool IsEnabled { get; }
}

/// <summary>
/// Port tools propre à un run.
/// Abstraction minimale — les définitions et dispatch sont gérés par les couches supérieures.
/// </summary>
public interface IRunToolsPort
{
    /// <summary>Vrai si des outils sont disponibles pour ce run.</summary>
    bool HasTools { get; }
}
