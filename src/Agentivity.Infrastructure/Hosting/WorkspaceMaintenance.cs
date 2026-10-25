namespace Agentivity.Infrastructure.Hosting;

/// <summary>
/// A planned update of the workspace: first announced (the people connected see when it starts), then the drain (no new
/// execution starts, those going on finish), and the new version is put in place once nothing runs. Nothing is planned unless
/// something plans it; the drain also comes when the server stops.
/// <para>
/// An execution going on is never cut: the update waits for it. An execution waiting for a person is not going on (it is kept
/// and comes back after the update), so an answer may still take it up.
/// </para>
/// </summary>
public sealed class WorkspaceMaintenance
{
    /// <summary>On every answer while an update is planned: when it starts (ISO 8601), so the Studio says it without asking.</summary>
    public const string Header = "X-Agentivity-Maintenance";

    private readonly Lock _lock = new();
    private DateTimeOffset? _startsAt;
    private bool _draining;
    private bool _stopping;

    /// <summary>When the planned update starts; null when none is planned.</summary>
    public DateTimeOffset? StartsAt
    {
        get { lock (_lock) return _startsAt; }
    }

    /// <summary>No new execution starts: the update is under way, or the server stops.</summary>
    public bool Draining
    {
        get { lock (_lock) return _draining || _stopping; }
    }

    /// <summary>The update planned (or none), and whether the drain has begun.</summary>
    public void Plan(DateTimeOffset? startsAt, bool draining)
    {
        lock (_lock)
        {
            _startsAt = startsAt;
            _draining = startsAt is not null && draining;
        }
    }

    /// <summary>The server stops: nothing new starts in the time it takes.</summary>
    public void Stopping()
    {
        lock (_lock) _stopping = true;
    }

    /// <summary>Why a new execution may not start now, or null.</summary>
    public object? Refusal() => Draining
        ? new { error = "workspace_maintenance", message = "The workspace is being updated: a new execution can start again in a few minutes. Nothing going on is lost." }
        : null;
}
