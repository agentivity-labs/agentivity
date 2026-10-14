using System.Collections.Immutable;
using Agentivity.Domain.Execution;

namespace Agentivity.Infrastructure.Execution.Executable;

/// <summary>
/// Statut générique d'un <see cref="IExecutable"/> après exécution.
/// La couche appelante (orchestrateur de team, workflow…) réagit uniquement à ce signal —
/// elle ignore les détails internes (HIL, ReAct, erreur transitoire…).
/// </summary>
public enum ExecutableStatus
{
    /// <summary>Exécution terminée avec succès — passer au suivant.</summary>
    Completed,

    /// <summary>
    /// Exécution en pause, en attente d'un événement externe (HIL, confirmation…).
    /// Le run parent doit s'arrêter et attendre avant de continuer.
    /// </summary>
    WaitingFor,

    /// <summary>
    /// Erreur non récupérable — stopper toute la chaîne immédiatement.
    /// </summary>
    Failed,
}

/// <summary>
/// Type de résultat commun au boundary IExecutable.
/// Produit par tout moteur (workflow, team, agent) et consommé par l'appelant.
/// </summary>
public sealed record ExecutorResult(
    /// <summary>
    /// Contenu textuel principal — toujours présent (vide si le composable produit uniquement
    /// des outputs structurés). Pour un agent : réponse LLM finale.
    /// Pour une team : FinalSynthesis ou dernier TaskOutput.Content.
    /// Pour un workflow : valeur de la clé "output" | "result" | "response" | "content" du state.
    /// </summary>
    string Content,
    /// <summary>
    /// Outputs nommés et typés.
    /// Keys = output names declared in the executable definition's Outputs.
    /// Values : string, long, double, bool, IReadOnlyList&lt;string&gt;.
    /// </summary>
    IReadOnlyDictionary<string, object?> Outputs,
    /// <summary>Artefacts structurés produits en parallèle du contenu textuel.</summary>
    IReadOnlyList<TaskArtifact> Artifacts,
    /// <summary>Hint de routing pour les orchestrateurs supportant le handoff. Null = pas de routing explicite.</summary>
    string? RoutingHint = null,
    /// <summary>
    /// True quand <see cref="RoutingHint"/> vient de l'action <c>ask_agent</c> plutôt que d'un
    /// <c>route</c> permanent — voir <c>TaskOutput.IsAskAgent</c>. Non utilisé hors du chemin team,
    /// au même titre que <see cref="RoutingHint"/> lui-même.
    /// </summary>
    bool IsAskAgent = false,
    /// <summary>
    /// True quand ce résultat répond à une question <c>ask_agent</c> en attente
    /// (<c>respond_agent</c>) — voir <c>TaskOutput.IsRespondAgent</c>. Non utilisé hors du chemin
    /// team.
    /// </summary>
    bool IsRespondAgent = false,
    /// <summary>Score de confiance [0,1] si une quality gate a été appliquée.</summary>
    double? Confidence = null,
    /// <summary>Raison de fin : "stop", "max_iterations", "cancelled", etc.</summary>
    string? FinishReason = null,
    /// <summary>
    /// Statut générique de l'executable à la fin de son exécution.
    /// Defaults to <see cref="ExecutableStatus.Completed"/> for backward compatibility.
    /// </summary>
    ExecutableStatus Status = ExecutableStatus.Completed,
    /// <summary>
    /// Token opaque identifiant ce pour quoi l'executable est suspendu (ex: requestId HIL,
    /// webhookId, timerId…). Propagé tel quel vers le haut de toute la hiérarchie d'exécution
    /// jusqu'au root handler. Null si Status != WaitingFor.
    /// </summary>
    string? WaitingForToken = null
)
{
    /// <summary>True si Content est vide ET Outputs est vide ET Artifacts est vide.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Content) && Outputs.Count == 0 && Artifacts.Count == 0;

    // ── Constructeurs de commodité ───────────────────────────────────────────

    /// <summary>Crée un résultat contenant uniquement du texte.</summary>
    public static ExecutorResult FromText(string content) => new(content, ImmutableDictionary<string, object?>.Empty, []);

    /// <summary>Crée un résultat suspendu en attente d'un événement externe identifié par <paramref name="token"/>.</summary>
    public static ExecutorResult WaitingFor(string token) => new(string.Empty, ImmutableDictionary<string, object?>.Empty, [], Status: ExecutableStatus.WaitingFor, WaitingForToken: token);

    /// <summary>Construit un ExecutorResult depuis un TaskOutput existant.</summary>
    public static ExecutorResult FromTaskOutput(TaskOutput output) => new(Content: output.Content, Outputs: ImmutableDictionary<string, object?>.Empty, Artifacts: output.Artifacts, RoutingHint: output.NextAgentTopologyPositionId, Confidence: output.Confidence);

    /// <summary>
    /// What a finished workflow gives back to what called it. Its text is what its last step produced: the last step that
    /// ran and gave a text under one of the usual names (output, result, response, content), each step's outputs being kept
    /// in the state as "step.name". A workflow none of whose steps gave one gives the text kept under one of these names
    /// alone, when there is one.
    /// </summary>
    public static ExecutorResult FromWorkflowState(IReadOnlyDictionary<string, object?> state, IReadOnlyList<string> executedSteps)
    {
        string[] names = ["output", "result", "response", "content"];
        var keys = executedSteps.Reverse().SelectMany(step => names.Select(name => $"{step}.{name}")).Concat(names);
        var content = keys.Select(key => state.TryGetValue(key, out var value) ? value?.ToString() : null).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return new(content ?? string.Empty, state, []);
    }
}
