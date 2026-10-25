using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Agentivity.Infrastructure.Identity;

/// <summary>What a publishable key reaches on a route (see <see cref="PublishableAccessExtensions.Publishable"/>).</summary>
public enum PublishableAccess
{
    /// <summary>The route answers it like anyone: nothing of the instance (the catalog of widgets, the icons).</summary>
    Anyone,

    /// <summary>Starting an execution: only of the element of the key, and only continuing an execution the key started.</summary>
    StartsItsElement,

    /// <summary>The route names an execution (<c>{executionId}</c>): only one the key started.</summary>
    ItsExecution,

    /// <summary>The route names a run (<c>{runId}</c>): only one of an execution the key started.</summary>
    ItsRun,

    /// <summary>The route names the stream of an execution (<c>{streamId}</c>): only the stream of one the key started.</summary>
    ItsStream,
}

/// <summary>Which route a publishable key reaches. A route that does not say is closed to it.</summary>
public sealed record PublishableAccessMetadata(PublishableAccess Access);

public static class PublishableAccessExtensions
{
    /// <summary>Opens the route to publishable keys, as far as <paramref name="access"/> says.</summary>
    public static TBuilder Publishable<TBuilder>(this TBuilder builder, PublishableAccess access)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new PublishableAccessMetadata(access));
}

/// <summary>Who started an execution, found from what a route names: the execution, one of its runs, or its stream.</summary>
public interface IExecutionStarters
{
    Task<Caller?> OfExecutionAsync(string executionId, CancellationToken cancellationToken);

    Task<Caller?> OfRunAsync(string runId, CancellationToken cancellationToken);

    Task<Caller?> OfStreamAsync(string streamId, CancellationToken cancellationToken);
}

/// <summary>
/// The space an execution is in (that of the element it runs), found from what a route names: the execution, one of its runs, or
/// its stream. Null when there is no such thing.
/// </summary>
public interface IExecutionSpaces
{
    Task<string?> OfExecutionAsync(string executionId, CancellationToken cancellationToken);

    Task<string?> OfRunAsync(string runId, CancellationToken cancellationToken);

    Task<string?> OfStreamAsync(string streamId, CancellationToken cancellationToken);
}

/// <summary>
/// How many executions each publishable key started in the last minute: a page's visitors start at most what its key allows.
/// Kept in memory: an instance runs on one machine.
/// </summary>
public sealed class PublishableStarts(TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _starts = new(StringComparer.Ordinal);

    /// <summary>Notes a start of <paramref name="keyId"/>; how long to wait when it is one too many (and then it is not noted).</summary>
    public TimeSpan? TryStart(string keyId, int perMinute)
    {
        var now = clock.GetUtcNow();
        var starts = _starts.GetOrAdd(keyId, _ => new Queue<DateTimeOffset>());
        lock (starts)
        {
            while (starts.Count > 0 && now - starts.Peek() >= Window)
                starts.Dequeue();
            if (starts.Count >= perMinute)
                return starts.Peek() + Window - now;
            starts.Enqueue(now);
            return null;
        }
    }
}
