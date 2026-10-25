using Agentivity.Infrastructure.Execution.Executable;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// The answer of a team: what it gives to whoever called it (a workflow, a team, the person) and what its run shows. Each
/// orchestration says what it is (see <c>AbstractTeamGraphOrchestrator.AnswerOf</c>); it is worked out once, when the run ends,
/// and kept with the run (<see cref="TeamProgress.Answer"/>). Everything that reads the result of a team reads it from here.
/// </summary>
public static class TeamAnswer
{
    /// <summary>The last answer that says something (a member that only handed over is not the answer).</summary>
    public static string LastAnswer(IReadOnlyList<TaskOutput> outputs) =>
        outputs.LastOrDefault(o => !string.IsNullOrWhiteSpace(o.Content))?.Content ?? string.Empty;

    /// <summary>The answer of a finished run.</summary>
    public static string Of(AgenticTeamResult result) => Pick(result.Answer, result.FinalSynthesis, result.Outputs);

    /// <summary>The answer kept with a run (a run kept before answers were: its synthesis, else its last answer).</summary>
    public static string Of(TeamProgress progress) => Pick(progress.Answer, progress.FinalSynthesis, progress.Outputs);

    /// <summary>The named outputs that go with the answer of a finished run: those of the member who concluded.</summary>
    public static IReadOnlyDictionary<string, object?> OutputsOf(AgenticTeamResult result) => result.AnswerOutputs;

    /// <summary>The named outputs kept with a run (a run kept before they were: none).</summary>
    public static IReadOnlyDictionary<string, object?> OutputsOf(TeamProgress progress) =>
        progress.AnswerOutputs?.ToDictionary(o => o.Key, o => NamedOutputs.Normalize(o.Value), StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, object?>();

    /// <summary>The named outputs of the last member who said something: the one who concluded, by default.</summary>
    public static IReadOnlyDictionary<string, object?> LastOutputs(IReadOnlyList<TaskOutput> outputs) =>
        outputs.LastOrDefault(o => !string.IsNullOrWhiteSpace(o.Content))?.Outputs ?? new Dictionary<string, object?>();

    private static string Pick(string? answer, string? finalSynthesis, IReadOnlyList<TaskOutput> outputs) =>
        !string.IsNullOrWhiteSpace(answer) ? answer
        : !string.IsNullOrWhiteSpace(finalSynthesis) ? finalSynthesis
        : LastAnswer(outputs);
}
