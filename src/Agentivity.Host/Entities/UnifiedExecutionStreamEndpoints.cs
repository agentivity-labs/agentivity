using Agentivity.Infrastructure.Identity;
using System.Text.Json;
using System.Threading.Channels;
using Agentivity.Host.AgUi;
using Agentivity.Infrastructure.Http;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Agentivity.Host.Entities;

/// <summary>
/// Unified SSE stream endpoints for all run types (workflow, team, agent).
/// Emits native AG-UI protocol frames (event: message, data: {"type":"..."}) on all channels.
/// Heartbeats (: heartbeat) are emitted every 20 s to keep connections alive during HIL waits.
/// Reconnect with Last-Event-ID to replay missed events.
/// Routes:
///   GET /api/v1/streams/runs/{runId}/events  — AG-UI events for a specific run (with replay)
///   GET /api/v1/streams/runs/events          — global stream across all runs
/// </summary>
internal static class ExecutionStreamEndpoints
{
    private const string RunsBase = "/api/v1/streams/runs";
    private const string ExecutionsBase = "/api/v1/streams/executions";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapUnifiedExecutionStreamApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var v1 = endpoints.MapV1();

        // GET /api/v1/streams/runs/{runId}/events
        v1.MapGet(
                "/streams/runs/{runId}/events",
                async (string runId, HttpContext httpContext, [FromServices] IRunEventBus eventBus) =>
                {
                    if (string.IsNullOrWhiteSpace(runId))
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await httpContext.Response.WriteAsJsonAsync(new { error = "runId is required." }).ConfigureAwait(false);
                        return;
                    }

                    await StreamRunEventsAsync(httpContext, runId, eventBus).ConfigureAwait(false);
                }
            )
            .WithName("StreamRunEvents")
            .WithSummary("Unified SSE stream for a specific run (workflow, team, agent) — supports replay via Last-Event-ID")
            .Publishable(Agentivity.Infrastructure.Identity.PublishableAccess.ItsRun)
            .WithTags("Streams");

        // GET /api/v1/streams/runs/events
        v1.MapGet(
                "/streams/runs/events",
                async (HttpContext httpContext, [FromServices] IRunEventBus eventBus) =>
                {
                    await StreamAllRunsEventsAsync(httpContext, eventBus).ConfigureAwait(false);
                }
            )
            .WithName("StreamAllRunsEvents")
            .WithSummary("Global SSE stream across all runs — supports replay via Last-Event-ID")
            .WithTags("Streams");

        // GET /api/v1/streams/{streamId}/events — primary SSE channel for a client (one per execution)
        v1.MapGet(
                "/streams/{streamId}/events",
                async (string streamId, HttpContext httpContext, [FromServices] IRunEventBus eventBus) =>
                {
                    if (string.IsNullOrWhiteSpace(streamId))
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await httpContext.Response.WriteAsJsonAsync(new { error = "streamId is required." }).ConfigureAwait(false);
                        return;
                    }

                    await StreamChannelEventsAsync(httpContext, streamId, eventBus).ConfigureAwait(false);
                }
            )
            .WithName("StreamChannelEvents")
            .WithSummary("Primary SSE channel for an execution — full AG-UI content, one channel per execution chain")
            .Publishable(Agentivity.Infrastructure.Identity.PublishableAccess.ItsStream)
            .WithTags("Streams");


        return endpoints;
    }

    // ── Per-run stream ────────────────────────────────────────────────────────

    private static async Task StreamRunEventsAsync(HttpContext httpContext, string runId, IRunEventBus eventBus)
    {
        using var subscription = eventBus.Subscribe(runId);
        var lastEventId = ParseLastEventId(httpContext);
        var replay = eventBus.Replay(runId, lastEventId, subscription.StartVersion);

        await WriteStreamAsync(httpContext, replay, subscription.Reader).ConfigureAwait(false);
    }

    // ── Global stream ─────────────────────────────────────────────────────────

    private static async Task StreamAllRunsEventsAsync(HttpContext httpContext, IRunEventBus eventBus)
    {
        using var subscription = eventBus.SubscribeAll();
        var lastEventId = ParseLastEventId(httpContext);
        var replay = eventBus.ReplayAll(lastEventId, subscription.StartVersion);

        // The global collection stream emits lightweight change triggers only.
        // Full AG-UI content (chat, HIL frames) is delivered exclusively on the per-run stream
        // to avoid each message being written twice when the frontend subscribes to both streams.
        await WriteCollectionStreamAsync(httpContext, replay, subscription.Reader).ConfigureAwait(false);
    }

    // ── Channel stream (primary — keyed by StreamId) ────────────────────────────

    private static async Task StreamChannelEventsAsync(HttpContext httpContext, string streamId, IRunEventBus eventBus)
    {
        using var subscription = eventBus.SubscribeChannel(streamId);
        var lastEventId = ParseLastEventId(httpContext);
        var replay = eventBus.ReplayChannel(streamId, lastEventId, subscription.StartVersion);
        // Channel streams span the full execution (potentially multiple nested runs).
        // We must NOT close the connection on terminal events from individual sub-runs
        // (e.g. an agent completing within a team run). The client is responsible for
        // closing the connection when the execution is done. Pass ignoreTerminal: true.
        await WriteStreamAsync(httpContext, replay, subscription.Reader, ignoreTerminal: true).ConfigureAwait(false);
    }

    // ── Core SSE writer (full AG-UI content) ─────────────────────────────────

    private static async Task WriteStreamAsync(HttpContext httpContext, RunEventReplayResult replay, ChannelReader<RunEvent> reader, bool ignoreTerminal = false)
    {
        var response = httpContext.Response;
        var ct = httpContext.RequestAborted;

        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";
        response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await response.StartAsync(ct).ConfigureAwait(false);
            // Single \n: apply the retry interval without dispatching a spurious empty event.
            await response.WriteAsync("retry: 5000\n", ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);

            // Replay buffered events if the client reconnects with Last-Event-ID
            if (replay.CanReplay && replay.Events.Count > 0)
            {
                foreach (var buffered in replay.Events)
                    await WriteEventAsync(response, buffered, ct).ConfigureAwait(false);
            }
            else if (!replay.CanReplay)
            {
                // History gap — tell the client to re-sync via REST
                await WriteControlEventAsync(response, "sync_required", new { version = replay.CurrentVersion, reason = "replay_unavailable" }, ct).ConfigureAwait(false);
            }

            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            var heartbeatTask = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();

            // readTask is created once and reused across heartbeats to avoid concurrent reads
            // on a SingleReader channel, which would throw and close the connection.
            var readTask = reader.ReadAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);

                if (completed == heartbeatTask)
                {
                    // Single \n: SSE comment to keep the connection alive, no event dispatched.
                    await response.WriteAsync(": heartbeat\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    heartbeatTask = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();
                    continue;
                }

                RunEvent @event;
                try
                {
                    @event = await readTask.ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                await WriteEventAsync(response, @event, ct).ConfigureAwait(false);

                // Drain any queued events without waiting
                while (reader.TryRead(out var queued))
                    await WriteEventAsync(response, queued, ct).ConfigureAwait(false);

                if (!ignoreTerminal && @event.Terminal)
                    break;

                // Only start the next read after the current event has been fully consumed
                readTask = reader.ReadAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    // ── Collection SSE writer (global / execution — change triggers only) ────
    //
    // The frontend subscribes to both the per-run stream and the collection stream.
    // Sending full AG-UI frames on both would cause every message to be displayed twice.
    // Collection streams therefore emit only lightweight `change` control events so the
    // frontend knows to trigger an HTTP status refresh, without duplicating content.

    private static async Task WriteCollectionStreamAsync(HttpContext httpContext, RunEventReplayResult replay, ChannelReader<RunEvent> reader)
    {
        var response = httpContext.Response;
        var ct = httpContext.RequestAborted;

        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";
        response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await response.StartAsync(ct).ConfigureAwait(false);
            await response.WriteAsync("retry: 5000\n", ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);

            if (!replay.CanReplay)
            {
                await WriteControlEventAsync(response, "sync_required", new { version = replay.CurrentVersion, reason = "replay_unavailable" }, ct).ConfigureAwait(false);
            }

            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            var heartbeatTask = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();
            var readTask = reader.ReadAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);

                if (completed == heartbeatTask)
                {
                    await response.WriteAsync(": heartbeat\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    heartbeatTask = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();
                    continue;
                }

                RunEvent @event;
                try
                {
                    @event = await readTask.ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                await WriteTriggerEventAsync(response, @event, ct).ConfigureAwait(false);

                while (reader.TryRead(out var queued))
                    await WriteTriggerEventAsync(response, queued, ct).ConfigureAwait(false);

                // The end of one run is not the end of this stream: it carries every run, and stays open until the client
                // leaves or the server stops.
                readTask = reader.ReadAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    // ── SSE helpers ───────────────────────────────────────────────────────────

    // Kinds that signal a change in run or node execution state.
    // After emitting their AG-UI frames, we also send a `sync_required` control event
    // so the frontend knows to refresh its HTTP status fetch.
    private static readonly HashSet<RunEventKind> _syncRequiredKinds =
    [
        RunEventKind.run_started,
        RunEventKind.run_updated,
        RunEventKind.run_completed,
        RunEventKind.run_failed,
        RunEventKind.run_cancelled,
        RunEventKind.node_started,
        RunEventKind.node_completed,
        RunEventKind.node_failed,
        RunEventKind.node_updated,
        RunEventKind.hil_gate_reached, // triggers FormsSection/ChatSection refresh
    ];

    /// <summary>
    /// Maps one internal <see cref="RunEvent"/> to zero or more AG-UI SSE frames and writes them.
    /// Each frame uses <c>event: message</c> as required by the AG-UI protocol.
    /// Events with no AG-UI equivalent (RunEventMapper returns empty) are silently skipped.
    /// After any execution-state-changing event a <c>sync_required</c> control event is also sent
    /// so the frontend triggers an HTTP status refresh.
    /// </summary>
    private static async Task WriteEventAsync(HttpResponse response, RunEvent @event, CancellationToken ct)
    {
        var frames = RunEventMapper.Map(@event).ToList();
        var logger = response.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("RunStream");
        if (frames.Count == 0)
        {
            logger?.LogDebug("SSE skip kind={Kind} runId={RunId} (no AG-UI mapping)", @event.Kind, @event.RunId);
        }
        foreach (var (_, data) in frames)
        {
            logger?.LogDebug("SSE → kind={Kind} runId={RunId} data={Data}", @event.Kind, @event.RunId, data);
            await response.WriteAsync($"id: {@event.Version}\n", ct).ConfigureAwait(false);
            await response.WriteAsync("event: message\n", ct).ConfigureAwait(false);
            await response.WriteAsync($"data: {data}\n\n", ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        // Emit sync_required after any event that changes execution or node state so the
        // frontend knows to refresh its HTTP run-status fetch.
        if (_syncRequiredKinds.Contains(@event.Kind))
        {
            await WriteControlEventAsync(
                    response,
                    "sync_required",
                    new
                    {
                        runId = @event.RunId,
                        version = @event.Version,
                        reason = "execution_state_changed",
                    },
                    ct
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteControlEventAsync(HttpResponse response, string eventName, object payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, _json);
        await response.WriteAsync($"event: {eventName}\n", ct).ConfigureAwait(false);
        await response.WriteAsync($"data: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits a lightweight <c>change</c> control event on collection/execution streams.
    /// These streams must not emit full AG-UI content frames because the frontend subscribes
    /// to both the per-run stream and the collection stream simultaneously; sending frames on
    /// both would cause every message to appear twice. Instead, the collection stream sends
    /// only a <c>change</c> trigger so the frontend performs an HTTP status fetch.
    /// </summary>
    private static async Task WriteTriggerEventAsync(HttpResponse response, RunEvent @event, CancellationToken ct)
    {
        await WriteControlEventAsync(
                response,
                "change",
                new
                {
                    topic = "execution",
                    runId = @event.RunId,
                    kind = @event.Kind.ToString(),
                    version = @event.Version,
                },
                ct
            )
            .ConfigureAwait(false);
    }

    private static long? ParseLastEventId(HttpContext httpContext)
    {
        var raw = httpContext.Request.Headers["Last-Event-ID"].ToString();
        return long.TryParse(raw, out var v) ? v : null;
    }
}
