namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Which exits of a step the run goes on by, once the step has run.
/// <para>
/// A step that succeeds goes on by its exits, except Error. A step that fails goes on by its Error exit when that exit leads
/// somewhere: the step comes to the error (its output "error" holds the message), and the workflow does not stop. A step that
/// fails with nothing on its Error exit stops the workflow. Waiting is not failing: a step that waits does not go by Error.
/// </para>
/// </summary>
internal static class StepExits
{
    public const string Error = "error";

    /// <summary>The exit a step chose, kept among what it produced (the control-flow steps choose theirs the same way).</summary>
    public const string ChosenExitKey = "__exit_port";

    /// <summary>
    /// What a step that failed comes to when its Error exit leads somewhere: the error, and that exit. Null when nothing is on
    /// its Error exit, and the failure stops the workflow.
    /// </summary>
    public static Dictionary<string, object?>? GoOnByError(WorkflowEntity definition, string nodeId, string message) =>
        definition.Connections.Any(c => string.Equals(c.From, nodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.FromPort, Error, StringComparison.OrdinalIgnoreCase))
            ? new(StringComparer.OrdinalIgnoreCase) { [Error] = message, [ChosenExitKey] = Error }
            : null;

    /// <summary>The exit a step chose, if it chose one.</summary>
    public static string? ChosenExit(IDictionary<string, object?> outputs) => outputs.TryGetValue(ChosenExitKey, out var exit) && exit is string port ? port : null;

    /// <summary>The step failed and went on by its Error exit.</summary>
    public static bool WentByError(IDictionary<string, object?> outputs) => string.Equals(ChosenExit(outputs), Error, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the run goes on by <paramref name="connection"/> after a step that came to <paramref name="outputs"/>.</summary>
    public static bool IsFollowed(WorkflowConnection connection, IDictionary<string, object?> outputs) =>
        ChosenExit(outputs) is { } chosen ? string.Equals(connection.FromPort, chosen, StringComparison.OrdinalIgnoreCase) : !string.Equals(connection.FromPort, Error, StringComparison.OrdinalIgnoreCase);
}
