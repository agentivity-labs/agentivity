using System.Collections.Concurrent;
using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Interactions;

public interface IInteractionRequestStore
{
    ValueTask SaveRequestAsync(InteractionRequest request, CancellationToken cancellationToken = default);

    ValueTask SaveResponseAsync(InteractionResponse response, CancellationToken cancellationToken = default);

    ValueTask<InteractionRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    ValueTask<InteractionResponse?> GetResponseAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>Waits until a response is saved for the given request, or the cancellation token fires.</summary>
    Task<InteractionResponse> WaitForResponseAsync(Guid requestId, CancellationToken cancellationToken = default);

    bool RemoveRequest(Guid requestId);

    /// <summary>Returns all pending requests (requests without a response).</summary>
    IReadOnlyList<InteractionRequest> GetPendingRequests();

    /// <summary>Returns all pending requests for a specific run.</summary>
    IReadOnlyList<InteractionRequest> GetPendingRequestsForRun(Guid runId);
}

/// <summary>
/// In-memory store for pending interaction requests and their responses,
/// backed by file persistence so requests survive app restarts.
/// Generic: usable by workflows, agents, teams or any run kind.
/// </summary>
public sealed class InteractionStore : IInteractionRequestStore
{
    private readonly ConcurrentDictionary<Guid, InteractionRequest> _requests = new();
    private readonly ConcurrentDictionary<Guid, InteractionResponse> _responses = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<InteractionResponse>> _waiters = new();
    private readonly string _interactionsPath;
    private readonly ILogger<InteractionStore> _logger;
    private readonly IRunEventBus? _eventBus;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public InteractionStore(IOptions<WaitingRunStorageOptions> options, ILogger<InteractionStore> logger, IRunEventBus? eventBus = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;
        _interactionsPath = Path.Combine(PathResolver.Resolve(options.Value.StoragePath), "waitingforinput", "interactions");

        RestoreFromDisk();
    }

    public async ValueTask SaveRequestAsync(InteractionRequest request, CancellationToken cancellationToken = default)
    {
        _requests[request.Id] = request;
        await PersistRequestAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.RunId != Guid.Empty && _eventBus is not null)
            PublishInteractionEvent(request.RunId, RunEventKind.interaction_created, request.Id, request.ChannelType);
    }

    public ValueTask SaveResponseAsync(InteractionResponse response, CancellationToken cancellationToken = default)
    {
        _responses[response.RequestId] = response;

        if (_waiters.TryRemove(response.RequestId, out var tcs))
            tcs.TrySetResult(response);

        if (_requests.TryGetValue(response.RequestId, out var request) && request.RunId != Guid.Empty && _eventBus is not null)
            PublishInteractionEvent(request.RunId, RunEventKind.interaction_resolved, request.Id, request.ChannelType);

        return ValueTask.CompletedTask;
    }

    public Task<InteractionResponse> WaitForResponseAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        if (_responses.TryGetValue(requestId, out var existing))
            return Task.FromResult(existing);

        var tcs = _waiters.GetOrAdd(requestId, _ => new TaskCompletionSource<InteractionResponse>(TaskCreationOptions.RunContinuationsAsynchronously));

        cancellationToken.Register(() =>
        {
            if (_waiters.TryRemove(requestId, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });

        return tcs.Task;
    }

    public ValueTask<InteractionRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        _requests.TryGetValue(requestId, out var request);
        return ValueTask.FromResult(request);
    }

    public ValueTask<InteractionResponse?> GetResponseAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        _responses.TryGetValue(requestId, out var response);
        return ValueTask.FromResult(response);
    }

    public IReadOnlyList<InteractionRequest> GetPendingRequests() => _requests.Values.Where(r => !_responses.ContainsKey(r.Id)).ToList();

    public IReadOnlyList<InteractionRequest> GetPendingRequestsForRun(Guid runId) => _requests.Values.Where(r => r.RunId == runId && !_responses.ContainsKey(r.Id)).ToList();

    public bool RemoveRequest(Guid requestId)
    {
        var hadResponse = _responses.TryRemove(requestId, out _);
        var removed = _requests.TryRemove(requestId, out var request);

        if (_waiters.TryRemove(requestId, out var tcs))
            tcs.TrySetCanceled();

        DeleteRequestFile(requestId);

        if (removed && request is not null && !hadResponse && request.RunId != Guid.Empty && _eventBus is not null)
            PublishInteractionEvent(request.RunId, RunEventKind.interaction_cancelled, request.Id, request.ChannelType);

        return removed;
    }

    private void PublishInteractionEvent(Guid runId, RunEventKind kind, Guid requestId, string channelType)
    {
        try
        {
            _eventBus!.Publish(
                new RunEvent(RunId: runId.ToString("D"), DefinitionId: string.Empty, RunKind: RunKind.Unknown, Kind: kind, EventName: kind.ToString(), OccurredAtUtc: DateTimeOffset.UtcNow, Payload: new Dictionary<string, object?> { ["requestId"] = requestId, ["channelType"] = channelType })
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish interaction event {Kind} for run {RunId}", kind, runId);
        }
    }

    private async ValueTask PersistRequestAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_interactionsPath);
            var filePath = GetRequestFilePath(request.Id);
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, request, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist interaction request {RequestId} to disk", request.Id);
        }
    }

    private void DeleteRequestFile(Guid requestId)
    {
        var filePath = GetRequestFilePath(requestId);
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete interaction request file {RequestId}", requestId);
            }
        }
    }

    private void RestoreFromDisk()
    {
        if (!Directory.Exists(_interactionsPath))
            return;

        var files = Directory.EnumerateFiles(_interactionsPath, "*.json", SearchOption.TopDirectoryOnly);
        var restored = 0;

        foreach (var filePath in files)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var request = JsonSerializer.Deserialize<InteractionRequest>(json, SerializerOptions);
                if (request is not null)
                {
                    _requests[request.Id] = request;
                    restored++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore interaction request from {Path}", filePath);
            }
        }

        if (restored > 0)
            _logger.LogInformation("Restored {Count} interaction requests from disk", restored);
    }

    private string GetRequestFilePath(Guid requestId) => Path.Combine(_interactionsPath, $"{requestId}.json");
}
