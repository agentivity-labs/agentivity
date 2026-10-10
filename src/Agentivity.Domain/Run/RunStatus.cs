using System.Text.Json.Serialization;

namespace Agentivity.Domain.Run;

/// <summary>
/// Unified lifecycle state for all run types: workflow, agent, team.
/// All entity types share the same state machine — workflows, agents and teams
/// can all be paused, suspended on a HIL gate, failed, or cancelled.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    /// <summary>Scheduled but not yet started.</summary>
    Pending,

    /// <summary>Actively executing.</summary>
    Running,

    /// <summary>Manually suspended (user or system pause).</summary>
    Paused,

    /// <summary>Suspended on a HIL gate, awaiting human response.</summary>
    WaitingForInput,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Terminated with an error.</summary>
    Failed,

    /// <summary>Explicitly cancelled by user or system.</summary>
    Cancelled,

    /// <summary>
    /// Was Running/Pending when the process died (crash or restart) — orphaned mid-flight.
    /// Set by startup recovery; can be manually re-entered from its last checkpoint.
    /// </summary>
    Interrupted,
}
