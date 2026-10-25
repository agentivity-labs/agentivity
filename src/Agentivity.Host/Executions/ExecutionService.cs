using Agentivity.Infrastructure.Identity;
using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Stores;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Execution.Executable;

namespace Agentivity.Host.Executions;

/// <summary>
/// Pure orchestration logic for starting agent/team/workflow executions.
/// Called by <see cref="ExecutionsEndpoints"/> and by <see cref="IApiStub"/> implementations.
/// An execution is the user-facing concept; runs are internal implementation details.
/// </summary>
public sealed class ExecutionService(IEdition? edition = null, MonthlyRuns? monthlyRuns = null, Agentivity.Infrastructure.Storage.Workspace.IWorkspaceObjectStore? workspace = null, ExecutionLimits? limits = null, Agentivity.Infrastructure.Hosting.WorkspaceMaintenance? maintenance = null)
{
    private const string ExecutionStreamBase = "/api/v1/streams/executions";

    public async Task<IResult> StartAsync(
        StartExecutionRequest body,
        IExecutableResolver resolver,
        IRunEventBus eventBus,
        ITeamEngine? teamEngine,
        IAgenticRepository catalogue,
        WorkflowEngine? workflowEngine,
        IRepository<ExecutionContext, ExecutionRecord> executionRepository,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        IRepository<RunContext, RunRecord>? runRepository = null,
        Caller? startedBy = null,
        Action<ExecutorResult?, Exception?>? finished = null
    )
    {
        if (string.IsNullOrWhiteSpace(body?.EntityId))
            return ValidationError("entity_id_required", "entityId is required.");

        if (runRepository is null)
            return Results.Problem("Run repository is not wired.", statusCode: 503);

        IExecutable executable;
        try
        {
            executable = await resolver.ResolveAsync(body.EntityId, ct).ConfigureAwait(false);
        }
        catch
        {
            return Results.NotFound(new { error = $"Entity '{body.EntityId}' not found." });
        }

        // A team that cannot be launched is refused before anything starts: no execution, no run left "Running" in the history.
        if (executable.Definition.Kind == ExecutableKind.Team && teamEngine is not null && await RefuseTeamAsync(executable.Definition.Id, teamEngine, catalogue, ct).ConfigureAwait(false) is { } refusal)
            return refusal;

        // No plan in force (a trial ended, a subscription cancelled): nothing new runs; an execution going on may still be answered.
        if (string.IsNullOrWhiteSpace(body.ExecutionId) && edition is { HasPlan: false })
            return Results.Json(EditionRefusals.NoPlan(), statusCode: StatusCodes.Status403Forbidden);

        // The workspace is being updated (or the server stops): a new one waits; what goes on finishes, an answer still takes one up.
        if (string.IsNullOrWhiteSpace(body.ExecutionId) && maintenance?.Refusal() is { } updating)
            return Results.Json(updating, statusCode: StatusCodes.Status503ServiceUnavailable);

        // Too many running at once, or the disk full: a new one waits (a continuation is the same execution).
        if (string.IsNullOrWhiteSpace(body.ExecutionId) && limits is not null && await limits.RefusalAsync(ct).ConfigureAwait(false) is { } refused)
            return Results.Json(refused, statusCode: StatusCodes.Status403Forbidden);

        // A new execution counts in the month, as far as the edition allows (a continuation is the same execution).
        if (string.IsNullOrWhiteSpace(body.ExecutionId) && monthlyRuns is not null && !await monthlyRuns.TryStartAsync(edition?.Limit(EditionLimits.MaxRunsPerMonth), ct).ConfigureAwait(false))
            return Results.Json(EditionRefusals.Limit(edition!, "executions a month", edition!.Limit(EditionLimits.MaxRunsPerMonth)!.Value), statusCode: StatusCodes.Status403Forbidden);

        // Resolve the execution's one live, durable identity FIRST — a continuation
        // (body.ExecutionId set) reuses it exactly as-is (StreamId/HilChannel/Telemetry/Trace
        // included); only a genuinely new execution gets a fresh one. RunContext.GetOrCreate then
        // resolves (or creates) the execution's one live run in the same step — never a candidate
        // that might not match.
        // Continuing an execution of a space the caller does not see is as continuing none.
        if (!string.IsNullOrWhiteSpace(body.ExecutionId) && await executionRepository.FindAsync(body.ExecutionId, ct).ConfigureAwait(false) is { } going && !SpaceScope.Access.Sees(going.SpaceId))
            return Results.NotFound(new { error = $"Execution '{body.ExecutionId}' not found." });

        // Running something needs the right to run in its space (not only in the space chosen).
        var elementSpace = await SpaceOfAsync(executable.Definition.Kind, executable.Definition.Id, ct).ConfigureAwait(false);
        if (!SpaceScope.May(Permissions.RunsStart, elementSpace))
            return Results.Json(AgentivityAuthentication.Refusal(Permissions.RunsStart), statusCode: StatusCodes.Status403Forbidden);

        var executionCtx = await ExecutionContext.FindOrCreate(executionRepository, body.ExecutionId, ct).ConfigureAwait(false);
        // A new execution is in the space of what it runs, and so is its history.
        executionCtx.PlaceIn(elementSpace);
        var runCtx = await RunContext.GetOrCreate(runRepository, ToRunKind(executable.Definition.Kind), body.EntityId, executionCtx, ct).ConfigureAwait(false);

        // Once started, it runs as a closed box, with what it calls, in its own space, whatever the spaces of who started it.
        using var unlimited = SpaceScope.Unlimited(executionCtx.SpaceId);
        return executable.Definition.Kind switch
        {
            ExecutableKind.Agent => await StartAgentAsync(executable, body, runCtx, eventBus, executionRepository, loggerFactory, ct, runRepository, startedBy, finished),
            ExecutableKind.Team when teamEngine is not null => await StartTeamAsync(executable, body, runCtx, teamEngine, catalogue, eventBus, executionRepository, loggerFactory, ct, startedBy, finished),
            ExecutableKind.Team => Results.Problem("Team engine module is not loaded.", statusCode: 503),
            ExecutableKind.Workflow when workflowEngine is not null => await StartWorkflowAsync(executable, body, runCtx, eventBus, executionRepository, loggerFactory, ct, startedBy, finished),
            ExecutableKind.Workflow => Results.Problem("Workflow engine module is not loaded.", statusCode: 503),
            _ => Results.BadRequest(new { error = $"Entity kind '{executable.Definition.Kind}' is not supported." }),
        };
    }

    // The space of the element run: an agent or a team in the agentic library, a workflow in the workflows (General when the
    // workspace does not say, as for an element of an edition without spaces).
    private async Task<string> SpaceOfAsync(ExecutableKind kind, string id, CancellationToken ct)
    {
        var type = kind switch
        {
            ExecutableKind.Agent => "agent",
            ExecutableKind.Team => "team",
            ExecutableKind.Workflow => "workflow.definition",
            _ => null,
        };
        if (workspace is null || type is null)
            return Agentivity.Infrastructure.Storage.Workspace.WorkspaceSpaces.Default;
        var found = await workspace.GetAsync(type, id, ct).ConfigureAwait(false);
        return found.Success ? found.Value?.Metadata.SpaceId ?? Agentivity.Infrastructure.Storage.Workspace.WorkspaceSpaces.Default : Agentivity.Infrastructure.Storage.Workspace.WorkspaceSpaces.Default;
    }

    private static async Task<IResult> StartAgentAsync(IExecutable executable, StartExecutionRequest body, RunContext runCtx, IRunEventBus eventBus, IRepository<ExecutionContext, ExecutionRecord> executionRepository, ILoggerFactory loggerFactory, CancellationToken ct, IRepository<RunContext, RunRecord>? runRepository = null, Caller? startedBy = null, Action<ExecutorResult?, Exception?>? finished = null)
    {
        // runCtx is already the execution's one live run instance — resolved once by
        // RunContext.GetOrCreate in StartAsync, before dispatching here.

        var runId = runCtx.RunId;
        var executionCtx = runCtx.Execution;
        var executionId = executionCtx.ExecutionId;
        var agentId = executable.Definition.Id;
        var createdAt = DateTimeOffset.UtcNow;

        await Agentivity.Infrastructure.Agentic.Agent.AgentProgressWriter.SetPayloadAsync(runRepository, runId, body.Input, ct: ct).ConfigureAwait(false);
        if (runRepository is not null)
        {
            var ctx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);

            ctx?.UpsertStep(agentId, step =>
            {
                step.Name = agentId;
                step.Kind = StepKind.Agent;
                step.Status = RunStatus.Running;
                step.Inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
            });

            if (executable.Definition is Agentivity.Infrastructure.Agentic.AgentEntity agentEntity)
            {
                var graph = agentEntity.Graph;
                var resourceNodeIds = new HashSet<string>(graph.Connections.Where(c => c.FromPort == "resource" || c.ToPort == "resource").Select(c => c.From), StringComparer.OrdinalIgnoreCase);
                foreach (var node in graph.Nodes)
                {
                    var isResource = resourceNodeIds.Contains(node.Id);
                    ctx?.UpsertStep(node.Id, step =>
                    {
                        step.Name = node.Id;
                        step.Kind = StepKind.Node;
                        step.SubType = isResource ? "resource" : null;
                        step.Status = RunStatus.Pending;
                    });
                }
                foreach (var conn in graph.Connections)
                    ctx?.UpsertTransition(new RunStepTransitionSnapshot(FromStepId: conn.From, ToStepId: conn.To, Content: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), Traversed: false, Timestamp: DateTimeOffset.UtcNow, FromPort: conn.FromPort, ToPort: conn.ToPort));
            }

            if (ctx is not null)
                ctx.Control = new RunControl();
        }

        executionCtx.AttachRun(agentId, "agent", body.Input, runId, startedBy);
        await executionRepository.AddAsync(executionCtx, ct).ConfigureAwait(false);

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
        if (body.Images is { Count: > 0 })
        {
            args["images"] = body.Images.Select(i => new Agentivity.Infrastructure.Core.Llm.ImageAttachment(i.Base64Data, i.MimeType)).ToList();
        }
        var logger = loggerFactory.CreateLogger("Executions.Agent");
        var entryNodeId = executable.Definition is Agentivity.Infrastructure.Agentic.AgentEntity ae ? ae.Graph.EntryNode : null;
        var streamId = runCtx.Execution.StreamId;

        _ = SpaceScope.RunUnlimited(async () =>
        {
            eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.run_started, EventName: "agent_run_started", OccurredAtUtc: DateTimeOffset.UtcNow, Payload: new { agentId, executionId }, ExecutionId: executionId, StreamId: streamId));

            if (runRepository is not null && entryNodeId is not null)
            {
                (await runRepository.FindAsync(runId, CancellationToken.None).ConfigureAwait(false))?.UpsertStep(entryNodeId, step => step.Status = RunStatus.Running);
                eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.node_updated, EventName: "node_updated", OccurredAtUtc: DateTimeOffset.UtcNow, ExecutionId: executionId, StreamId: streamId));
            }

            try
            {
                // A failed run fails its execution, as every caller of a child does.
                var result = (await executable.ExecuteAsync(args, runCtx, CancellationToken.None).ConfigureAwait(false)).ThrowIfFailed(executable.Definition);

                // Node/step status must be committed BEFORE the top-level run status flips to
                // Completed: the frontend poll loop stops the instant it observes the run as
                // completed, so if the node write landed after that, the node freezes on its
                // last-seen (Running/blue) color forever — see AgentTurnOutcomeReporter, which
                // owns the top-level flip below.
                if (result.Status != ExecutableStatus.WaitingFor)
                {
                    var completedCtx = runRepository is null ? null : await runRepository.FindAsync(runId, CancellationToken.None).ConfigureAwait(false);
                    completedCtx?.UpsertStep(agentId, step => { step.Status = RunStatus.Completed; step.Outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["output"] = result.Content }; });
                    if (entryNodeId is not null)
                        completedCtx?.UpsertStep(entryNodeId, step => step.Status = RunStatus.Completed);
                    await Agentivity.Infrastructure.Agentic.Agent.AgentProgressWriter.SetPayloadAsync(runRepository, runId, body.Input, result.Content).ConfigureAwait(false);
                    if (entryNodeId is not null)
                        eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.node_updated, EventName: "node_updated", OccurredAtUtc: DateTimeOffset.UtcNow, ExecutionId: executionId, StreamId: streamId));
                }

                var completed = await AgentTurnOutcomeReporter.ReportAsync(runRepository, eventBus, runId, agentId, executionId, result.Status, result.Content, CancellationToken.None, streamId).ConfigureAwait(false);
                if (!completed)
                {
                    executionCtx.SetState("paused");
                    await executionRepository.AddAsync(executionCtx, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    executionCtx.SetState("completed");
                    await executionRepository.AddAsync(executionCtx, CancellationToken.None).ConfigureAwait(false);
                }
                finished?.Invoke(result, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent execution {ExecutionId} failed.", executionId);
                var failedAt = DateTimeOffset.UtcNow;
                await SetRunStateAsync(runRepository, runId, RunStatus.Failed, ex.Message, failedAt).ConfigureAwait(false);
                var failedCtx = runRepository is null ? null : await runRepository.FindAsync(runId, CancellationToken.None).ConfigureAwait(false);
                failedCtx?.AppendError(ex.Message);
                if (entryNodeId is not null)
                    failedCtx?.UpsertStep(entryNodeId, step => { step.Status = RunStatus.Failed; step.Error = ex.Message; });
                executionCtx.SetState("failed");
                await executionRepository.AddAsync(executionCtx, CancellationToken.None).ConfigureAwait(false);
                eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.run_failed, EventName: "agent_run_failed", OccurredAtUtc: failedAt, Payload: new { error = ex.Message }, Terminal: true, ExecutionId: executionId, StreamId: streamId));
                finished?.Invoke(null, ex);
            }
        }, CancellationToken.None);

        return Results.Accepted($"/api/v1/executions/{executionId}", BuildResponse(executionId, runCtx.Execution.StreamId, runId, agentId, "agent", "active", createdAt));
    }

    private static async Task<IResult> StartTeamAsync(IExecutable executable, StartExecutionRequest body, RunContext runCtx, ITeamEngine teamEngine, IAgenticRepository catalogue, IRunEventBus eventBus, IRepository<ExecutionContext, ExecutionRecord> executionRepository, ILoggerFactory loggerFactory, CancellationToken ct, Caller? startedBy, Action<ExecutorResult?, Exception?>? finished)
    {
        // The team was checked before the execution was created (RefuseTeamAsync).
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
        return await RunExecutableAsync(executable, body, runCtx, args, "team", eventBus, executionRepository, loggerFactory, ct, startedBy, finished).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the team cannot be launched, or null when it can. TeamExecutable.ExecuteAsync does not check it, so it is checked here —
    /// before the execution and its run are created, so that a refusal leaves nothing behind.
    /// </summary>
    private static async Task<IResult?> RefuseTeamAsync(string teamId, ITeamEngine teamEngine, IAgenticRepository catalogue, CancellationToken ct)
    {
        var teamResult = await catalogue.GetTeamAsync(teamId, ct).ConfigureAwait(false);
        if (!teamResult.IsSuccess)
            return Results.NotFound(new { error = $"Team '{teamId}' not found.", detail = teamResult.Error });

        var validation = await teamEngine.ValidateAsync(teamResult.Value!, ct).ConfigureAwait(false);
        return validation.IsValid ? null : Results.Problem(string.Join("; ", validation.Errors), statusCode: 422, title: "Team configuration is invalid");
    }

    private static async Task<IResult> StartWorkflowAsync(IExecutable executable, StartExecutionRequest body, RunContext runCtx, IRunEventBus eventBus, IRepository<ExecutionContext, ExecutionRecord> executionRepository, ILoggerFactory loggerFactory, CancellationToken ct, Caller? startedBy, Action<ExecutorResult?, Exception?>? finished)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = body.Input };
        return await RunExecutableAsync(executable, body, runCtx, args, "workflow", eventBus, executionRepository, loggerFactory, ct, startedBy, finished).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared run/track/complete/fail path for entity kinds (team, workflow) whose
    /// <see cref="IExecutable"/> adapter needs no kind-specific post-execution handling beyond
    /// reading back <see cref="RunContext.Status"/> — unlike the agent path, which has its own
    /// node/step bookkeeping, HIL <c>WaitingFor</c> handling, and <c>AgentTurnOutcomeReporter</c>
    /// reporting and stays a separate method (<see cref="StartAgentAsync"/>) for that reason.
    /// </summary>
    private static async Task<IResult> RunExecutableAsync(IExecutable executable, StartExecutionRequest body, RunContext runCtx, IReadOnlyDictionary<string, object?> args, string entityKind, IRunEventBus eventBus, IRepository<ExecutionContext, ExecutionRecord> executionRepository, ILoggerFactory loggerFactory, CancellationToken ct, Caller? startedBy, Action<ExecutorResult?, Exception?>? finished = null)
    {
        var runId = runCtx.RunId;
        var executionCtx = runCtx.Execution;
        var executionId = executionCtx.ExecutionId;
        var entityId = executable.Definition.Id;
        var createdAt = DateTimeOffset.UtcNow;
        var logger = loggerFactory.CreateLogger($"Executions.{entityKind}");

        executionCtx.AttachRun(entityId, entityKind, body.Input, runId, startedBy);
        await executionRepository.AddAsync(executionCtx, ct).ConfigureAwait(false);

        _ = SpaceScope.RunUnlimited(async () =>
        {
            try
            {
                // A failed run fails its execution, as every caller of a child does.
                var result = (await executable.ExecuteAsync(args, runCtx, CancellationToken.None).ConfigureAwait(false)).ThrowIfFailed(executable.Definition);
                // A caught, structured failure (e.g. LlmCallException converted to a Failed graph
                // task) never rethrows here — it only flips RunContext.Status; and a run that asked a
                // person something returns here waiting for the answer. Reading that back rather than
                // assuming "completed" is what keeps the Execution's state consistent with its run's
                // actual outcome (see ExecutionContext.StateOf).
                executionCtx.SetState(ExecutionContext.StateOf(runCtx.Status));
                await executionRepository.AddAsync(executionCtx, CancellationToken.None).ConfigureAwait(false);
                finished?.Invoke(result, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{EntityKind} execution {ExecutionId} failed.", entityKind, executionId);
                executionCtx.SetState("failed");
                await executionRepository.AddAsync(executionCtx, CancellationToken.None).ConfigureAwait(false);
                finished?.Invoke(null, ex);
            }
        }, CancellationToken.None);

        return Results.Accepted($"/api/v1/executions/{executionId}", BuildResponse(executionId, runCtx.Execution.StreamId, runId, entityId, entityKind, "active", createdAt));
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Unified response returned by all start endpoints.
    /// <c>streamUrl</c> always points to the execution-level SSE stream.
    /// <c>runId</c> is provided for callers that need it for inspector/status polling.
    /// </summary>
    private static async ValueTask SetRunStateAsync(IRepository<RunContext, RunRecord>? runRepository, string runId, RunStatus state, string? error = null, DateTimeOffset? completedAt = null, CancellationToken ct = default)
    {
        if (runRepository is null) return;
        var ctx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
        if (ctx is null) return;
        ctx.SetStatus(state, error, completedAt);
        await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
    }

    internal static object BuildResponse(string executionId, string streamId, string runId, string entityId, string entityKind, string state, DateTimeOffset createdAt) =>
        new
        {
            executionId,
            streamId,
            runId,
            entityId,
            entityKind,
            state,
            createdAt,
            streamUrl = $"/api/v1/streams/{streamId}/events",
        };

    internal static RunKind ToRunKind(ExecutableKind kind) =>
        kind switch
        {
            ExecutableKind.Team => RunKind.Team,
            ExecutableKind.Workflow => RunKind.Workflow,
            _ => RunKind.Agent,
        };

    private static IResult ValidationError(string code, string message) =>
        Results.BadRequest(new { status = "failed", code, message });
}

/// <summary>Request to start an execution of any entity (agent, team, workflow).</summary>
public sealed record StartExecutionRequest(
    /// <summary>ID of the entity to execute.</summary>
    string EntityId,
    /// <summary>Free-text input / goal.</summary>
    string Input,
    /// <summary>Optional existing execution id to join (e.g. for HIL resume).</summary>
    string? ExecutionId = null,
    int? MaxAgentIterations = null,
    /// <summary>
    /// Images attached to this turn (vision). Agent-kind executions only for now — see
    /// <see cref="ExecutionService.StartAgentAsync"/>. A Host-layer DTO kept distinct from the
    /// internal <see cref="Agentivity.Infrastructure.Core.Llm.ImageAttachment"/> type so the
    /// request contract doesn't couple to the LLM-layer type.
    /// </summary>
    IReadOnlyList<ImageAttachmentDto>? Images = null
);

/// <summary>An image attached to a chat turn, sent inline as base64 (no upload endpoint).</summary>
public sealed record ImageAttachmentDto(string Base64Data, string MimeType);
