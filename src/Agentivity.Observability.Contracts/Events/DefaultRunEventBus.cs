using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Agentivity.Observability.Contracts.Events;

/// <summary>
/// Replayable implementation of <see cref="IRunEventBus"/>.
/// Each run has its own versioned history with bounded retention.
/// Supports replay, multi-subscriber, and a global "all runs" stream.
/// </summary>
public sealed class DefaultRunEventBus : IRunEventBus
{
    private const int MaxHistoryPerRun = 256;
    private static readonly TimeSpan EmptyStreamRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletedStreamRetention = TimeSpan.FromMinutes(20);

    private static readonly IReadOnlySet<RunEventKind> TerminalKinds = new HashSet<RunEventKind> { RunEventKind.run_completed, RunEventKind.run_failed, RunEventKind.run_cancelled };

    private readonly ConcurrentDictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamState> _channelStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamState _allRunsStream = new();

    // ── IRunEventBus ──────────────────────────────────────────────────────────

    public void Publish(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        CleanupExpiredStreams();

        var state = _streams.GetOrAdd(@event.RunId, static _ => new StreamState());
        var terminal = TerminalKinds.Contains(@event.Kind) || @event.Terminal;
        var versioned = state.Publish(@event, terminal);

        // Fan-out to the execution's SSE channel (keyed by StreamId from ExecutionContext).
        // StreamId is the single stable channel identifier for the entire execution chain.
        if (!string.IsNullOrWhiteSpace(@event.StreamId))
        {
            var channelState = _channelStreams.GetOrAdd(@event.StreamId, static _ => new StreamState());
            channelState.Publish(versioned, terminal: false);
        }

        // Also publish to the global stream, augmenting metadata with runId
        _allRunsStream.Publish(versioned, terminal: false);
    }

    public RunEventSubscription Subscribe(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        CleanupExpiredStreams();
        // Exclusive: evict any existing subscribers so a reconnecting client doesn't cause
        // duplicate delivery when the old connection is still alive.
        return _streams.GetOrAdd(runId, static _ => new StreamState()).SubscribeExclusive();
    }

    public RunEventSubscription SubscribeAll()
    {
        CleanupExpiredStreams();
        return _allRunsStream.Subscribe();
    }

    public RunEventReplayResult Replay(string runId, long? lastEventVersion, long maxVersionInclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!_streams.TryGetValue(runId, out var state))
            return new RunEventReplayResult(true, 0, []);

        return state.Replay(lastEventVersion, maxVersionInclusive);
    }

    public RunEventReplayResult ReplayAll(long? lastEventVersion, long maxVersionInclusive) => _allRunsStream.Replay(lastEventVersion, maxVersionInclusive);

    public void Remove(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_streams.TryRemove(runId, out var state))
            state.Complete();
    }

    public RunEventSubscription SubscribeChannel(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        CleanupExpiredStreams();
        return _channelStreams.GetOrAdd(streamId, static _ => new StreamState()).Subscribe();
    }

    public RunEventReplayResult ReplayChannel(string streamId, long? lastEventVersion, long maxVersionInclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        if (!_channelStreams.TryGetValue(streamId, out var state))
            return new RunEventReplayResult(true, 0, []);

        return state.Replay(lastEventVersion, maxVersionInclusive);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void CleanupExpiredStreams()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (runId, state) in _streams)
        {
            if (!state.CanCleanup(now))
                continue;

            if (_streams.TryRemove(runId, out var removed))
                removed.Complete();
        }
    }

    // ── StreamState ───────────────────────────────────────────────────────────

    private sealed class StreamState
    {
        private readonly object _gate = new();
        private readonly LinkedList<RunEvent> _history = new();
        private readonly Dictionary<Guid, Channel<RunEvent>> _subscribers = new();
        private long _nextVersion = 1;
        private long _oldestAvailableVersion = 1;
        private DateTimeOffset _lastTouched = DateTimeOffset.UtcNow;
        private bool _terminal;

        public long CurrentVersion
        {
            get
            {
                lock (_gate)
                {
                    return _nextVersion - 1;
                }
            }
        }

        public RunEventSubscription Subscribe()
        {
            var id = Guid.NewGuid();
            Channel<RunEvent> channel;
            long startVersion;
            bool alreadyTerminal;

            lock (_gate)
            {
                _lastTouched = DateTimeOffset.UtcNow;
                alreadyTerminal = _terminal;
                channel = Channel.CreateUnbounded<RunEvent>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false,
                    }
                );
                startVersion = _nextVersion - 1;

                if (!alreadyTerminal)
                    _subscribers[id] = channel;
            }

            if (alreadyTerminal)
            {
                // Stream is already done: close the channel immediately so that the SSE reader
                // does not hang waiting for events that will never come.
                // The caller is expected to use Last-Event-ID + replay to get historical events.
                channel.Writer.TryComplete();
            }

            return new RunEventSubscription(startVersion, channel.Reader, () => Unsubscribe(id));
        }

        /// <summary>
        /// Subscribes exclusively: any existing subscribers are evicted (their channels completed)
        /// before the new subscription is created. This prevents duplicate delivery when a client
        /// reconnects before the previous SSE connection has been torn down.
        /// </summary>
        public RunEventSubscription SubscribeExclusive()
        {
            var id = Guid.NewGuid();
            Channel<RunEvent> channel;
            long startVersion;
            bool alreadyTerminal;
            List<Channel<RunEvent>>? evicted = null;

            lock (_gate)
            {
                _lastTouched = DateTimeOffset.UtcNow;
                alreadyTerminal = _terminal;

                // Evict existing subscribers
                if (_subscribers.Count > 0)
                {
                    evicted = [.. _subscribers.Values];
                    _subscribers.Clear();
                }

                channel = Channel.CreateUnbounded<RunEvent>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false,
                    }
                );
                startVersion = _nextVersion - 1;

                if (!alreadyTerminal)
                    _subscribers[id] = channel;
            }

            if (evicted is not null)
            {
                foreach (var old in evicted)
                    old.Writer.TryComplete();
            }

            if (alreadyTerminal)
                channel.Writer.TryComplete();

            return new RunEventSubscription(startVersion, channel.Reader, () => Unsubscribe(id));
        }

        public RunEvent Publish(RunEvent @event, bool terminal)
        {
            RunEvent versioned;
            List<Channel<RunEvent>> targets;

            lock (_gate)
            {
                // A standalone agent reuses the same RunId across turns (Story 1: RunId == ExecutionId).
                // If this stream was already marked terminal by a previous turn and a new,
                // non-terminal event arrives, a new turn has started on the same RunId — reopen the stream.
                if (_terminal && !terminal)
                    _terminal = false;

                var version = _nextVersion++;
                versioned = @event with { Version = version };

                _history.AddLast(versioned);
                while (_history.Count > MaxHistoryPerRun)
                {
                    _history.RemoveFirst();
                    _oldestAvailableVersion++;
                }

                _terminal |= terminal;
                _lastTouched = DateTimeOffset.UtcNow;
                targets = [.. _subscribers.Values];
            }

            foreach (var ch in targets)
            {
                ch.Writer.TryWrite(versioned);
                if (terminal)
                    ch.Writer.TryComplete();
            }

            return versioned;
        }

        public RunEventReplayResult Replay(long? lastEventVersion, long maxVersionInclusive)
        {
            lock (_gate)
            {
                _lastTouched = DateTimeOffset.UtcNow;
                var current = _nextVersion - 1;

                // No Last-Event-ID: replay all history so the client sees events that were
                // published before the SSE connection was established (e.g. node_started/
                // node_completed events that fired before the frontend opened the stream).
                // Duplicate delivery on reconnect is prevented by SubscribeExclusive which
                // evicts the old connection before the new one subscribes.
                if (!lastEventVersion.HasValue)
                {
                    var all = _history.Where(e => e.Version <= maxVersionInclusive).ToList();
                    return new RunEventReplayResult(true, current, all);
                }

                if (lastEventVersion.Value + 1 < _oldestAvailableVersion)
                    return new RunEventReplayResult(false, current, []);

                var events = _history.Where(e => e.Version > lastEventVersion.Value && e.Version <= maxVersionInclusive).ToList();

                return new RunEventReplayResult(true, current, events);
            }
        }

        public bool CanCleanup(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_subscribers.Count > 0)
                    return false;

                var retention = _terminal ? CompletedStreamRetention : EmptyStreamRetention;
                return now - _lastTouched >= retention;
            }
        }

        public void Complete()
        {
            List<Channel<RunEvent>> targets;
            lock (_gate)
            {
                targets = [.. _subscribers.Values];
                _subscribers.Clear();
            }

            foreach (var ch in targets)
                ch.Writer.TryComplete();
        }

        private void Unsubscribe(Guid id)
        {
            Channel<RunEvent>? channel = null;
            lock (_gate)
            {
                if (_subscribers.Remove(id, out var removed))
                    channel = removed;

                _lastTouched = DateTimeOffset.UtcNow;
            }

            channel?.Writer.TryComplete();
        }
    }
}
