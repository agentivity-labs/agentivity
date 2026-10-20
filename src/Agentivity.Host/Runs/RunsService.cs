using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agentivity.Host.Runs;

/// <summary>
/// Pure orchestration logic for starting agent/team/workflow runs via the unified run dispatcher.
/// Shared by the real <c>/api/v1/runs</c> endpoint and by <see cref="IApiStub"/> implementations.
/// </summary>
public sealed class RunsService
{
    private const string StreamBase = "/api/v1/streams/runs";

    public async Task<IResult> StartRunAsync(
        UnifiedRunRequest body,
        IExecutableResolver resolver,
        IRunEventBus eventBus,
        ITeamEngine? teamEngine,
        IAgenticRepository catalogue,
        WorkflowEngine? workflowEngine,
        IExecutionRecordStore executionStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        IRepository<RunContext, RunRecord>? runRepository = null
    )
    {
        if (string.IsNullOrWhiteSpace(body?.EntityId))
            return ValidationError("entity_id_required", "entityId is required.");
        // Input is optional for workflows (they define their own input structure).
        // For agents and teams, input is validated downstream in the respective start handlers.

        // Resolve the executable to determine its kind.
        IExecutable executable;
        try
        {
            executable = await resolver.ResolveAsync(body.EntityId, ct).ConfigureAwait(false);
        }
        catch
        {
            return Results.NotFound(new { error = $"Entity '{body.EntityId}' not found." });
        }

        var runCtx = RunContext.StartNew(ToRunKind(executable.Definition.Kind), body.EntityId, body.ExecutionId);

        return executable.Definition.Kind switch
        {
            ExecutableKind.Agent => await StartAgentRunAsync(executable, body, runCtx, eventBus, executionStore, loggerFactory, ct, runRepository),
            ExecutableKind.Team when teamEngine is not null => await StartTeamRunAsync(executable, body, runCtx, teamEngine, catalogue, eventBus, executionStore, loggerFactory, ct),
            ExecutableKind.Team => Results.Problem("Team engine module is not loaded.", statusCode: 503),
            ExecutableKind.Workflow when workflowEngine is not null => await StartWorkflowRunAsync(executable, body, runCtx, workflowEngine, executionStore, loggerFactory, ct),
            ExecutableKind.Workflow => Results.Problem("Workflow engine module is not loaded.", statusCode: 503),
            _ => Results.BadRequest(new { error = $"Entity kind '{executable.Definition.Kind}' is not supported by the unified run dispatcher." }),
        };
    }

    private static async Task<IResult> StartAgentRunAsync(IExecutable executable, UnifiedRunRequest body, RunContext runCtx, IRunEventBus eventBus, IExecutionRecordStore executionStore, ILoggerFactory loggerFactory, CancellationToken ct, IRepository<RunContext, RunRecord>? runRepository = null)
    {
        // Register in the generic run registry + attach a RunControl so pause/unpause work.
        // Resolve to the run's own live instance FIRST (a re-join re-enters the SAME run — see
        // RunContext.FindOrRegister) — every local below is derived from the resolved object.
        if (runRepository is not null)
            runCtx = await RunContext.FindOrRegister(runRepository, runCtx, ct).ConfigureAwait(false);

        var runId = runCtx.RunId;
        var executionId = runCtx.Execution.ExecutionId;
        var agentId = executable.Definition.Id;
        var createdAt = DateTimeOffset.UtcNow;

        await AgentProgressWriter.SetPayloadAsync(runRepository, runId, body.Input, ct: ct).ConfigureAwait(false);
        if (runRepository is not null)
        {
            var ctx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);

            // Register the top-level agent step (for inspector/trace).
            var agentDisplayName = (executable.Definition as Agentivity.Infrastructure.Agentic.AgentEntity)?.Name;
            ctx?.UpsertStep(
                agentId,
                step =>
                {
                    step.Name = string.IsNullOrWhiteSpace(agentDisplayName) ? agentId : agentDisplayName;
                    step.Kind = StepKind.Agent;
                    step.Status = RunStatus.Running;
                    step.Inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
                }
            );

            // Register each graph node so the graph editor can animate them,
            // mirroring what WorkflowExecutionLoop does for workflow nodes.
            if (executable.Definition is Agentivity.Infrastructure.Agentic.AgentEntity agentEntity)
            {
                var graph = agentEntity.Graph;
                var resourceNodeIds = new HashSet<string>(graph.Connections.Where(c => c.FromPort == "resource" || c.ToPort == "resource").Select(c => c.From), StringComparer.OrdinalIgnoreCase);

                foreach (var node in graph.Nodes)
                {
                    var isResource = resourceNodeIds.Contains(node.Id);
                    ctx?.UpsertStep(
                        node.Id,
                        step =>
                        {
                            step.Name = node.Id;
                            step.Kind = StepKind.Node;
                            step.SubType = isResource ? "resource" : null;
                            step.Status = RunStatus.Pending;
                        }
                    );
                }

                // Register connections with port info so resource node states can be derived.
                foreach (var conn in graph.Connections)
                {
                    ctx?.UpsertTransition(
                        new RunStepTransitionSnapshot(FromStepId: conn.From, ToStepId: conn.To, Content: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), Traversed: false, Timestamp: DateTimeOffset.UtcNow, FromPort: conn.FromPort, ToPort: conn.ToPort)
                    );
                }
            }

            if (ctx is not null)
                ctx.Control = new RunControl();
        }

        // Create or update the execution record.
        var existingExecution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
        var execution = existingExecution is not null
            ? existingExecution with
            {
                CurrentRunId = runId,
                UpdatedAt = createdAt,
                RunIds = [.. existingExecution.RunIds, runId],
            }
            : new ExecutionRecord
            {
                ExecutionId = executionId,
                EntityId = agentId,
                EntityKind = "agent",
                Input = body.Input,
                State = "active",
                CurrentRunId = runId,
                RunIds = [runId],
                StreamId = runCtx.Execution.StreamId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
        await executionStore.SaveAsync(execution, ct).ConfigureAwait(false);

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
        var logger = loggerFactory.CreateLogger("Runs.Agent");

        // Capture the entry node ID so the fire-and-forget can update node states.
        var entryNodeId = executable.Definition is Agentivity.Infrastructure.Agentic.AgentEntity ae ? ae.Graph.EntryNode : null;

        // The frontend's chat/run panels subscribe to the StreamId-keyed channel
        // (/api/v1/streams/{streamId}/events), not the RunId-keyed stream — every RunEvent this
        // method publishes must carry it, or the client that's actually listening never sees it
        // (confirmed in production for the analogous resume path, see StandaloneAgentResumableEngine).
        var streamId = runCtx.Execution.StreamId;

        _ = Task.Run(
            async () =>
            {
                eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.run_started, EventName: "agent_run_started", OccurredAtUtc: DateTimeOffset.UtcNow, Payload: new { agentId, executionId }, ExecutionId: executionId, StreamId: streamId));

                // Mark the entry node as Running so the graph editor animates it.
                if (runRepository is not null && entryNodeId is not null)
                {
                    (await runRepository.FindAsync(runId, CancellationToken.None).ConfigureAwait(false))?.UpsertStep(entryNodeId, step => step.Status = RunStatus.Running);
                    eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.node_updated, EventName: "node_updated", OccurredAtUtc: DateTimeOffset.UtcNow, ExecutionId: executionId, StreamId: streamId));
                }

                try
                {
                    // A failed run fails its execution, as every caller of a child does.
                var result = (await executable.ExecuteAsync(args, runCtx, CancellationToken.None).ConfigureAwait(false)).ThrowIfFailed(executable.Definition);
                    var completedAt = DateTimeOffset.UtcNow;

                    // Node/step status must be committed BEFORE the top-level run status flips to
                    // Completed: the frontend poll loop stops the instant it observes the run as
                    // completed, so if the node write landed after that, the node freezes on its
                    // last-seen (Running/blue) color forever — see AgentTurnOutcomeReporter, which
                    // owns the top-level flip below.
                    if (result.Status != ExecutableStatus.WaitingFor)
                    {
                        var completedCtx = runRepository is null ? null : await runRepository.FindAsync(runId, CancellationToken.None).ConfigureAwait(false);
                        completedCtx?.UpsertStep(
                            agentId,
                            step =>
                            {
                                step.Status = RunStatus.Completed;
                                step.Outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["output"] = result.Content };
                            }
                        );
                        // Mark entry node as Completed.
                        if (entryNodeId is not null)
                            completedCtx?.UpsertStep(entryNodeId, step => step.Status = RunStatus.Completed);
                        await AgentProgressWriter.SetPayloadAsync(runRepository, runId, body.Input, result.Content).ConfigureAwait(false);
                        if (entryNodeId is not null)
                            eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.node_updated, EventName: "node_updated", OccurredAtUtc: completedAt, ExecutionId: executionId, StreamId: streamId));
                    }

                    var completed = await AgentTurnOutcomeReporter.ReportAsync(runRepository, eventBus, runId, agentId, executionId, result.Status, result.Content, CancellationToken.None, streamId).ConfigureAwait(false);
                    if (!completed)
                    {
                        await executionStore.SaveAsync(execution with { State = "paused", UpdatedAt = completedAt }, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await executionStore.SaveAsync(execution with { State = "completed", UpdatedAt = completedAt }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Agent run {RunId} failed.", runId);
                    var failedAt = DateTimeOffset.UtcNow;
                    await SetRunStateAsync(runRepository, runId, RunStatus.Failed, ex.Message, failedAt).ConfigureAwait(false);
                    var failedCtx = runRepository is null ? null : await runRepository.FindAsync(runId, CancellationToken.None).ConfigureAwait(false);
                    failedCtx?.AppendError(ex.Message);
                    if (entryNodeId is not null)
                        failedCtx?.UpsertStep(
                            entryNodeId,
                            step =>
                            {
                                step.Status = RunStatus.Failed;
                                step.Error = ex.Message;
                            }
                        );
                    await executionStore.SaveAsync(execution with { State = "failed", UpdatedAt = failedAt }, CancellationToken.None).ConfigureAwait(false);
                    eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.run_failed, EventName: "agent_run_failed", OccurredAtUtc: failedAt, Payload: new { error = ex.Message }, Terminal: true, ExecutionId: executionId, StreamId: streamId));
                }
            },
            CancellationToken.None
        );

        return Results.Accepted($"/api/v1/runs/{runId}", BuildResponse(runId, executionId, agentId, "agent", "running", createdAt));
    }

    private static async Task<IResult> StartTeamRunAsync(IExecutable executable, UnifiedRunRequest body, RunContext runCtx, ITeamEngine teamEngine, IAgenticRepository catalogue, IRunEventBus eventBus, IExecutionRecordStore executionStore, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var runId = runCtx.RunId;
        var executionId = runCtx.Execution.ExecutionId;
        var teamId = executable.Definition.Id;

        var teamResult = await catalogue.GetTeamAsync(teamId, ct).ConfigureAwait(false);
        if (!teamResult.IsSuccess)
            return Results.NotFound(new { error = $"Team '{teamId}' not found.", detail = teamResult.Error });

        var definition = teamResult.Value!;
        var validation = await teamEngine.ValidateAsync(definition, ct).ConfigureAwait(false);
        if (!validation.IsValid)
            return Results.Problem(string.Join("; ", validation.Errors), statusCode: 422, title: "Team configuration is invalid");

        var createdAt = DateTimeOffset.UtcNow;
        var run = new TeamRunRequest(RunId: runId, Team: definition, Input: body.Input, Options: new TeamRunRequestOptions(body.MaxAgentIterations ?? 10), RunContext: runCtx);
        var logger = loggerFactory.CreateLogger("Runs.Team");

        // Create or update the execution record.
        var existingExecution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
        var execution = existingExecution is not null
            ? existingExecution with
            {
                CurrentRunId = runId,
                UpdatedAt = createdAt,
                RunIds = [.. existingExecution.RunIds, runId],
            }
            : new ExecutionRecord
            {
                ExecutionId = executionId,
                EntityId = teamId,
                EntityKind = "team",
                Input = body.Input,
                State = "active",
                CurrentRunId = runId,
                RunIds = [runId],
                StreamId = runCtx.Execution.StreamId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
        await executionStore.SaveAsync(execution, ct).ConfigureAwait(false);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await teamEngine.ExecuteAsync(run, CancellationToken.None).ConfigureAwait(false);
                    // Finished, waiting for a person's answer, or failed: the execution says where its run stands.
                    await executionStore.SaveAsync(execution with { State = Agentivity.Infrastructure.Execution.Executions.ExecutionContext.StateOf(runCtx.Status), UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Team run {RunId} failed.", runId);
                    await executionStore.SaveAsync(execution with { State = "failed", UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
                }
            },
            CancellationToken.None
        );

        return Results.Accepted($"/api/v1/runs/{runId}", BuildResponse(runId, executionId, teamId, "team", "running", createdAt));
    }

    private static async Task<IResult> StartWorkflowRunAsync(IExecutable executable, UnifiedRunRequest body, RunContext runCtx, WorkflowEngine workflowEngine, IExecutionRecordStore executionStore, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var runIdStr = runCtx.RunId;
        var runIdGuid = Guid.TryParse(runIdStr, out var g) ? g : Guid.NewGuid();
        var executionId = runCtx.Execution.ExecutionId;
        var workflowId = executable.Definition.Id;
        var logger = loggerFactory.CreateLogger("Runs.Workflow");
        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
        var createdAt = DateTimeOffset.UtcNow;

        // Create or update ExecutionRecord so the execution is queryable by executionId.
        var existingExecution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
        var execution = existingExecution is not null
            ? existingExecution with
            {
                CurrentRunId = runIdStr,
                UpdatedAt = createdAt,
                RunIds = [.. existingExecution.RunIds, runIdStr],
            }
            : new ExecutionRecord
            {
                ExecutionId = executionId,
                EntityId = workflowId,
                EntityKind = "workflow",
                Input = body.Input ?? string.Empty,
                State = "active",
                CurrentRunId = runIdStr,
                RunIds = [runIdStr],
                StreamId = runCtx.Execution.StreamId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
        await executionStore.SaveAsync(execution, ct).ConfigureAwait(false);

        // Fire-and-forget: the run outlives the HTTP request.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await workflowEngine.ExecuteAsync(runIdGuid, workflowId, input, CancellationToken.None, parentContext: runCtx).ConfigureAwait(false);
                    // Finished, waiting for a person's answer, or failed: the execution says where its run stands.
                    await executionStore.SaveAsync(execution with { State = Agentivity.Infrastructure.Execution.Executions.ExecutionContext.StateOf(runCtx.Status), UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Workflow run {RunId} failed.", runIdGuid);
                    await executionStore.SaveAsync(execution with { State = "failed", UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None).ConfigureAwait(false);
                }
            },
            CancellationToken.None
        );

        return Results.Accepted($"/api/v1/runs/{runIdGuid}", BuildResponse(runIdGuid.ToString(), executionId, workflowId, "workflow", "running", createdAt));
    }

    private static async ValueTask SetRunStateAsync(IRepository<RunContext, RunRecord>? runRepository, string runId, RunStatus state, string? error = null, DateTimeOffset? completedAt = null, CancellationToken ct = default)
    {
        if (runRepository is null) return;
        var ctx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
        if (ctx is null) return;
        ctx.SetStatus(state, error, completedAt);
        await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
    }

    private static object BuildResponse(string runId, string executionId, string entityId, string entityKind, string state, DateTimeOffset createdAt) =>
        new
        {
            runId,
            executionId,
            entityId,
            entityKind,
            state,
            createdAt,
            streamUrl = $"{StreamBase}/{runId}/events",
        };

    internal static RunKind ToRunKind(ExecutableKind kind) =>
        kind switch
        {
            ExecutableKind.Team => RunKind.Team,
            ExecutableKind.Workflow => RunKind.Workflow,
            _ => RunKind.Agent,
        };

    private static IResult ValidationError(string code, string message) =>
        Results.BadRequest(
            new
            {
                status = "failed",
                code,
                message,
            }
        );
}

/// <summary>Unified run request — works for any entity kind (agent or team).</summary>
public sealed record UnifiedRunRequest(
    /// <summary>ID of the agent or team to run.</summary>
    string EntityId,
    /// <summary>Free-text input / goal for the run.</summary>
    string Input,
    /// <summary>Optional existing execution id to join. A new execution is created if omitted.</summary>
    string? ExecutionId = null,
    int? MaxAgentIterations = null
);
