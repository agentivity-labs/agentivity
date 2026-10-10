using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>What the agent decided in its turn-conclusion call.</summary>
public enum TurnConclusionKind
{
    /// <summary>The call did not match any offered choice — only a tool result is recorded, nothing else changes.</summary>
    Invalid,

    /// <summary>The turn ends here: the agent is done, or hands control to its orchestrator (see <see cref="TurnConclusionOutcome.Action"/>).</summary>
    Conclude,

    /// <summary>The agent needs an answer from the human user before it can continue.</summary>
    NeedsInput,
}

/// <summary>
/// The engine-facing result of interpreting one turn-conclusion call. The engine only understands
/// "conclude" and "needs input"; <see cref="Action"/>, <see cref="Target"/> and <see cref="Notes"/> are
/// opaque to it — a protocol defined by whoever supplied the tool (a team engine, for instance) — and are
/// handed back untouched to the orchestrator through <c>AgentRunResult</c>.
/// </summary>
/// <param name="Kind">The decision.</param>
/// <param name="ToolResult">Text recorded as the tool call's own result in the conversation.</param>
/// <param name="Action">Protocol-defined action name for an orchestrator, null for a plain conclusion.</param>
/// <param name="Target">Protocol-defined target for <paramref name="Action"/>, if any.</param>
/// <param name="AlwaysDeliverDraft">
/// True: the turn's draft text is delivered even when empty (a plain conclusion). False: it is delivered
/// only when it has content (a hand-off that may legitimately carry no text).
/// </param>
/// <param name="Notes">Best-effort side information that rides along with any decision; never blocks anything.</param>
public sealed record TurnConclusionOutcome(TurnConclusionKind Kind, string ToolResult, string? Action = null, string? Target = null, bool AlwaysDeliverDraft = true, IReadOnlyList<string>? Notes = null);

/// <summary>
/// The "what happens next" tool an agent is forced to call once a turn ends in plain text. The engine
/// knows nothing about its choices beyond conclude / need-input: any richer protocol (teams, routing…)
/// is supplied by an implementation of this interface.
/// </summary>
public interface ITurnConclusionTool
{
    /// <summary>The tool definition offered to the model in the constrained follow-up call.</summary>
    JsonObject Definition { get; }

    /// <summary>
    /// True when the tool offers choices beyond conclude / need-input (e.g. handing off to a teammate), which
    /// makes an explicit decision mandatory after a free-text turn instead of letting the agent trail off.
    /// </summary>
    bool OffersOrchestration { get; }

    /// <summary>A copy of this tool without the choices that depend on the turn's own draft text (used after that text was already delivered live).</summary>
    ITurnConclusionTool WithoutContentDependentActions();

    /// <summary>Interprets the arguments of the model's call.</summary>
    TurnConclusionOutcome Interpret(string argumentsJson);
}

/// <summary>Builds the turn-conclusion tool for agents whose context does not supply its own.</summary>
public interface ITurnConclusionToolFactory
{
    ITurnConclusionTool CreateDefault(bool allowNeedsInput);
}

/// <summary>The built-in factory: a plain "done / needs_input" decision, no orchestration.</summary>
public sealed class GenericTurnConclusionToolFactory : ITurnConclusionToolFactory
{
    public static readonly GenericTurnConclusionToolFactory Instance = new();

    public ITurnConclusionTool CreateDefault(bool allowNeedsInput) => new GenericTurnConclusionTool(allowNeedsInput);
}

/// <summary>
/// Minimal turn-conclusion tool: the agent states whether it is done, or needs the human user to answer.
/// Deliberately 100% structured — the text delivered to the user is always the main turn's own response;
/// this tool only records the decision.
/// </summary>
public sealed class GenericTurnConclusionTool : ITurnConclusionTool
{
    public const string ToolName = "agent_next_step";
    public const string ActionArgument = "action";
    public const string DoneAction = "done";
    public const string NeedsInputAction = "needs_input";

    private const string NeedsInputTableLine = "\n- 'needs_input' | Your last message needs an answer from the actual HUMAN USER before you can continue.";
    private const string NeedsInputDescriptionClause = " you need the actual human user to answer something ('needs_input'),";

    private readonly bool _allowNeedsInput;

    public GenericTurnConclusionTool(bool allowNeedsInput = true)
    {
        _allowNeedsInput = allowNeedsInput;
        Definition = Build(allowNeedsInput);
    }

    public JsonObject Definition { get; }

    public bool OffersOrchestration => false;

    public ITurnConclusionTool WithoutContentDependentActions() => new GenericTurnConclusionTool(allowNeedsInput: false);

    public TurnConclusionOutcome Interpret(string argumentsJson)
    {
        var action = TryGetAction(argumentsJson);
        if (string.Equals(action, DoneAction, StringComparison.Ordinal))
            return new TurnConclusionOutcome(TurnConclusionKind.Conclude, "Conclusion recorded.");
        if (_allowNeedsInput && string.Equals(action, NeedsInputAction, StringComparison.Ordinal))
            return new TurnConclusionOutcome(TurnConclusionKind.NeedsInput, "Clarification request recorded.");

        return new TurnConclusionOutcome(TurnConclusionKind.Invalid, $"Invalid '{ToolName}' call: '{ActionArgument}' must match one of the listed choices.");
    }

    private static JsonObject Build(bool allowNeedsInput)
    {
        var actions = new List<string> { DoneAction };
        if (allowNeedsInput)
            actions.Add(NeedsInputAction);

        var table = "Available choices (action | meaning):\n- 'done' | Your answer is complete — nothing more to do.";
        if (allowNeedsInput)
            table += NeedsInputTableLine;

        var inputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [ActionArgument] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"What happens next.\n{table}",
                    ["enum"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
                },
                // Not read by the platform: it only gives the model an outlet for its urge to keep writing,
                // without which some models abandon the structured call and answer in free text instead.
                ["message"] = new JsonObject { ["type"] = "string", ["description"] = "Optional scratch space — not shown to the user and not used by the platform." },
            },
            ["required"] = new JsonArray(ActionArgument),
        };

        var description = "State what happens next after the message you just wrote: you're done," + (allowNeedsInput ? NeedsInputDescriptionClause : string.Empty);
        description = description.TrimEnd(',') + ".";

        return LlmToolDefinitionJson.Create(ToolName, description, inputSchema.ToJsonString(), isRoutingTool: true);
    }

    private static string? TryGetAction(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.TryGetProperty(ActionArgument, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
