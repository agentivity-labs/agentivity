using System.Threading.Channels;
using Agentivity.Module.PromptEngine.Models;
using Agentivity.Module.PromptEngine.Services;
using Agentivity.Module.PromptEngine.Storage;

namespace Agentivity.Module.PromptEngine.Background;

public sealed class OptimizationWorker : BackgroundService
{
    private readonly Channel<string> _channel;
    private readonly IPromptEngineRepository _store;
    private readonly PromptOptimizer _optimizer;
    private readonly ILogger<OptimizationWorker> _logger;

    public OptimizationWorker(Channel<string> channel, IPromptEngineRepository store, PromptOptimizer optimizer, ILogger<OptimizationWorker> logger)
    {
        _channel = channel;
        _store = store;
        _optimizer = optimizer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessRunAsync(runId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Optimization run {RunId} failed", runId);
                    await TryMarkFailedAsync(runId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected when the host stops while this worker is idle, waiting on the channel for
            // the next queued optimization run — not an error.
        }
    }

    private async Task ProcessRunAsync(string runId, CancellationToken ct)
    {
        var run = await _store.GetOptimizationRunAsync(runId.ToString(), ct).ConfigureAwait(false);
        if (run is null || run.Status == OptimizationStatus.Cancelled)
            return;

        var promptEntity = await _store.GetPromptAsync(run.PromptId, ct).ConfigureAwait(false);
        if (promptEntity is null)
        {
            run.Status = OptimizationStatus.Failed;
            await _store.SaveOptimizationRunAsync(run, ct).ConfigureAwait(false);
            return;
        }

        var playgroundRequest = run.PlaygroundRequest ?? new PromptPlaygroundRunRequest();
        var basePrompt = BuildEffectivePrompt(promptEntity, playgroundRequest);
        var optimizationScenario = new TestCase
        {
            Id = Guid.NewGuid().ToString(),
            PromptId = promptEntity.Id,
            Name = "playground-optimization",
            Inputs = playgroundRequest.Inputs,
            ExpectedOutput = playgroundRequest.ExpectedOutput,
            CreatedAt = run.StartedAt,
        };

        var scenarios = new List<TestCase> { optimizationScenario };

        run.Status = OptimizationStatus.Running;
        await _store.SaveOptimizationRunAsync(run, ct).ConfigureAwait(false);

        var currentPrompt = basePrompt;

        for (var i = 1; i <= run.MaxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            var freshRun = await _store.GetOptimizationRunAsync(runId.ToString(), ct).ConfigureAwait(false);
            if (freshRun?.Status == OptimizationStatus.Cancelled)
                return;

            var iteration = await _optimizer.RunIterationAsync(currentPrompt, scenarios, run.EvaluationCriteria, run.Iterations, i, ct).ConfigureAwait(false);

            run.Iterations.Add(iteration);
            run.CurrentIteration = i;

            if (!run.BestScore.HasValue || iteration.Score > run.BestScore.Value)
            {
                run.BestScore = iteration.Score;
                run.BestCandidateComponents = new Dictionary<string, string>(iteration.CandidateComponents);
            }

            var bestIterationSnapshot = run.Iterations.OrderByDescending(candidate => candidate.Score).ThenByDescending(candidate => candidate.IterationNumber).First();

            currentPrompt = new PromptEntity(basePrompt.Id, basePrompt.Name)
            {
                Content = bestIterationSnapshot.CandidateComponents.GetValueOrDefault("content") ?? basePrompt.Content,
                SystemPrompt = bestIterationSnapshot.CandidateComponents.GetValueOrDefault("system_prompt") ?? basePrompt.SystemPrompt,
                ResponseSchema = bestIterationSnapshot.CandidateComponents.GetValueOrDefault("response_schema") ?? basePrompt.ResponseSchema,
                EvaluationPrompt = basePrompt.EvaluationPrompt,
                LlmCredentialId = basePrompt.LlmCredentialId,
                Category = basePrompt.Category,
                Description = basePrompt.Description,
                Version = basePrompt.Version,
                VersionName = basePrompt.VersionName,
                CurrentVersionSnapshotId = basePrompt.CurrentVersionSnapshotId,
                Variables = basePrompt.Variables,
                Metadata = basePrompt.Metadata,
                CreatedAt = basePrompt.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await _store.SaveOptimizationRunAsync(run, ct).ConfigureAwait(false);
        }

        run.Status = OptimizationStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        await UpdateCurrentVersionSnapshotAsync(currentPrompt, run, ct).ConfigureAwait(false);
        await _store.SaveOptimizationRunAsync(run, ct).ConfigureAwait(false);

        _logger.LogInformation("Optimization run {RunId} completed. Best score: {Score}", runId, run.BestScore);
    }

    private static PromptEntity BuildEffectivePrompt(PromptEntity persistedPrompt, PromptPlaygroundRunRequest request) =>
        new(persistedPrompt.Id, string.IsNullOrWhiteSpace(request.Name) ? persistedPrompt.Name : request.Name)
        {
            Content = string.IsNullOrWhiteSpace(request.Content) ? persistedPrompt.Content : request.Content,
            SystemPrompt = request.SystemPrompt ?? persistedPrompt.SystemPrompt,
            ResponseSchema = request.ResponseSchema ?? persistedPrompt.ResponseSchema,
            EvaluationPrompt = request.EvaluationPrompt ?? persistedPrompt.EvaluationPrompt,
            LlmCredentialId = persistedPrompt.LlmCredentialId,
            Category = persistedPrompt.Category,
            Description = persistedPrompt.Description,
            Version = persistedPrompt.Version,
            VersionName = persistedPrompt.VersionName,
            CurrentVersionSnapshotId = persistedPrompt.CurrentVersionSnapshotId,
            Variables = persistedPrompt.Variables,
            Metadata = persistedPrompt.Metadata,
            CreatedAt = persistedPrompt.CreatedAt,
            UpdatedAt = persistedPrompt.UpdatedAt,
        };

    private async Task UpdateCurrentVersionSnapshotAsync(PromptEntity prompt, OptimizationRun run, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt.CurrentVersionSnapshotId) || run.Iterations.Count == 0)
            return;

        var snapshot = await _store.GetPromptVersionSnapshotAsync(prompt.CurrentVersionSnapshotId, ct).ConfigureAwait(false);
        if (snapshot is null)
            return;

        var bestIteration = run.Iterations.OrderByDescending(iteration => iteration.Score).ThenByDescending(iteration => iteration.IterationNumber).First();

        snapshot.LastKnownScore = bestIteration.Score;
        snapshot.LastKnownCost = bestIteration.CostEstimate;
        snapshot.LastKnownTokensUsed = bestIteration.TokensUsed;
        snapshot.LastExecutedAt = bestIteration.LastExecutedAt ?? run.CompletedAt;
        snapshot.ExecutionConfig = bestIteration.ExecutionConfig;

        await _store.SavePromptVersionSnapshotAsync(snapshot, ct).ConfigureAwait(false);
    }

    private async Task TryMarkFailedAsync(string runId, CancellationToken ct)
    {
        try
        {
            var run = await _store.GetOptimizationRunAsync(runId.ToString(), ct).ConfigureAwait(false);
            if (run is not null && run.Status == OptimizationStatus.Running)
            {
                run.Status = OptimizationStatus.Failed;
                run.CompletedAt = DateTime.UtcNow;
                await _store.SaveOptimizationRunAsync(run, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark optimization run {RunId} as failed", runId);
        }
    }
}
