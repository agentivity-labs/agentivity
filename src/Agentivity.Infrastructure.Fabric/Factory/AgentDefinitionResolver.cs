using System.Text.Json;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// <see cref="IAgentDefinitionResolver"/> for the standalone/team shells (<see cref="StandaloneAgent"/>,
/// <c>TeamAgent</c>) — resolves all upstream values from an <see cref="AgentEntity"/>'s
/// <c>Graph</c> (its <c>ai.node</c> parameters and connected resources).
/// <para>
/// Ported verbatim (mechanical move) from the former <c>AgentExecutable.ExecuteAgentAsync</c> body.
/// </para>
/// </summary>
/// <param name="registryRunId">
/// When set, overrides the registry key used for trace/checkpoint lookups.
/// Used by team agents where the registry is indexed by the team run ID,
/// not the agent sub-run ID that lives in <see cref="RunContext.RunId"/>.
/// </param>
public sealed class AgentDefinitionResolver(AgentEntity agentDefinition, AgentHost agentHost, IRunEventBus? eventBus = null, IAgentTelemetryEmitter? telemetryEmitter = null, Agentivity.Infrastructure.Repository.IRepository<RunContext, RunRecord>? runRepository = null, string? registryRunId = null) : IAgentDefinitionResolver
{
    private const string AiNodeType = "ai.node";
    private const string DefaultModeId = "conversational";

    // ── Sync bridging helpers over the async IRepository surface ────────────
    // FindAsync's cache-hit path is synchronous under the hood (ConcurrentDictionary
    // lookup, no I/O) — same reasoning applied elsewhere in this codebase (WorkflowEngine.cs).

    private RunContext? GetRun(string runId) => runRepository?.FindAsync(runId).AsTask().GetAwaiter().GetResult();

    private void AppendStepLog(string runId, string stepId, string message) => GetRun(runId)?.AppendStepLog(stepId, message);

    private Agentivity.Domain.Run.DebugTraceItem? GetStepTrace(string runId, string stepId) => GetRun(runId)?.GetStepTrace(stepId);

    private void SetStepTrace(string runId, string stepId, Agentivity.Domain.Run.DebugTraceItem? trace) => GetRun(runId)?.SetStepTrace(stepId, trace);

    private RunControl? GetRunControl(string runId) => GetRun(runId)?.Control;

    public ValueTask<ResolvedAgentDefinition> ResolveAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        // ── Node parameters ───────────────────────────────────────────────────
        var aiNodeParams = agentDefinition.Graph.Nodes.FirstOrDefault(n => string.Equals(n.NodeType, AiNodeType, StringComparison.OrdinalIgnoreCase))?.Parameters is { ValueKind: JsonValueKind.Object } p ? (JsonElement?)p : null;
        var aiNodeId = agentDefinition.Graph.Nodes.FirstOrDefault(n => string.Equals(n.NodeType, AiNodeType, StringComparison.OrdinalIgnoreCase))?.Id;

        // ── LLM provider ──────────────────────────────────────────────────────
        var llmResolved = aiNodeId is not null ? AgentGraphResolver.ResolveLlmProvider(agentDefinition, aiNodeId) : null;
        if (llmResolved is null)
            throw new InvalidOperationException($"Agent '{agentDefinition.Name}' has no LLM model node connected to the 'llm_model' port.");
        var (llmProvider, llmBinding) = llmResolved.Value;

        // ── Mode runtime ──────────────────────────────────────────────────────
        var modeId = ReadString(aiNodeParams, "agent_mode") ?? DefaultModeId;
        if (!agentHost.TryGetModeRuntime(modeId, out var modeRuntime) || modeRuntime is null)
        {
            if (!agentHost.TryGetModeRuntime(DefaultModeId, out modeRuntime) || modeRuntime is null)
                throw new InvalidOperationException($"No '{modeId}' (or fallback '{DefaultModeId}') mode runtime is registered.");
        }

        // ── Runtime parameters ────────────────────────────────────────────────
        var runtimeParameters = AgentRuntimeParameterResolver.Resolve(new AgentParameterReader(name => ReadString(aiNodeParams, name), name => ReadDouble(aiNodeParams, name), name => ReadInt(aiNodeParams, name), name => ReadBool(aiNodeParams, name)));

        // ── Policies + Guardrails ─────────────────────────────────────────────
        var policyResolver = new AgentPolicyResolver();
        var externalPolicyLayers = aiNodeId is not null ? AgentGraphResolver.ResolvePolicyLayers(agentDefinition, aiNodeId) : [];
        var resolvedPolicies = policyResolver.Resolve(AgentPolicyLayerFactory.BuildPolicyLayers(modeRuntime, externalPolicyLayers));
        var resolvedGuardrails = aiNodeId is not null ? AgentGraphResolver.ResolveGuardrails(agentDefinition, aiNodeId) : (IReadOnlyList<AgentGuardrailDefinition>)[];

        // ── Memory / RAG ──────────────────────────────────────────────────────
        var memoryPort = AgentMemoryResolver.TryCreate(agentDefinition);
        var resolvedPrompt = ResolvePrompt(aiNodeParams, args);

        return ResolveCoreAsync(args, context, aiNodeParams, aiNodeId, modeRuntime, runtimeParameters, resolvedPolicies, resolvedGuardrails, memoryPort, resolvedPrompt, llmProvider, llmBinding, ct);
    }

    private async ValueTask<ResolvedAgentDefinition> ResolveCoreAsync(
        IReadOnlyDictionary<string, object?> args,
        RunContext context,
        JsonElement? aiNodeParams,
        string? aiNodeId,
        IAgenticModeRuntime modeRuntime,
        AgentRuntimeParameters runtimeParameters,
        ResolvedAgentPolicies resolvedPolicies,
        IReadOnlyList<AgentGuardrailDefinition> resolvedGuardrails,
        AgentMemoryResolver? memoryPort,
        string resolvedPrompt,
        Agentivity.Infrastructure.Workflows.Resources.ILlmProvider llmProvider,
        Agentivity.Infrastructure.Workflows.Resources.ResourceBinding llmBinding,
        CancellationToken ct
    )
    {
        // ── Memory / RAG ──────────────────────────────────────────────────────
        var memoryContext = string.Empty;
        if (memoryPort is not null)
        {
            var memoryTopK = ReadInt(aiNodeParams, "memory_top_k") ?? 3;
            memoryContext = await memoryPort.RetrieveAsync(resolvedPrompt, memoryTopK, ct).ConfigureAwait(false);
        }

        // ── Runtime port ──────────────────────────────────────────────────────
        var toolPort = aiNodeId is not null ? StandaloneAgentToolProxy.TryCreate(agentDefinition, aiNodeId) : null;

        var runId = Guid.TryParse(context.RunId, out var rid) ? rid : Guid.NewGuid();
        var stepId = context.EntityId;
        var runIdStr = runId.ToString("D");
        // Use override run ID when provided (team agents: registry is keyed by team run ID, not sub-run ID).
        var registryKey = registryRunId ?? runIdStr;

        var policyPort = new ComposedAgentPolicyProxy(agentHost.PolicyRuntime, resolvedPolicies);
        IAgentGuardrailPort? guardrailPort = resolvedGuardrails.Count > 0 ? new ComposedAgentGuardrailProxy(agentHost, resolvedGuardrails, llmProvider, llmBinding) : null;
        // Enrich the incoming context in-place — RunContext is mutable, no copy needed.
        context.Memory = memoryPort;
        context.Policy = policyPort;
        context.Guardrail = guardrailPort;
        var correlation = context.ToCorrelationContext();

        // publishStatus is NOT wired to the chat/message channel (that would diverge from
        // the workflow path and break AgentChatMessageEventTests). It is wired to the
        // run registry step log so the inspector can display progress messages.
        Action<string>? publishStatus = runRepository is null
            ? null
            : msg => AppendStepLog(registryKey, stepId, msg);

        // Real (non-no-op) trace proxy: emits llm_call_started/llm_call_completed telemetry
        // around each LLM call, mirroring WorkflowAgentLlmCoordinator (the AiNode path).
        var runtimePort = new StandaloneAgentRuntimeProxy(llmProvider, llmBinding, toolPort, publishStatus, telemetryEmitter, correlation);
        var contextServices = new AgentRunServices
        {
            RunId = runId,
            CorrelationContext = correlation,
            TelemetryEmitter = telemetryEmitter,
            PublishStatus = publishStatus,
            // Checkpoint: blocks when the run is Paused, resumes when Unpaused.
            Checkpoint = runRepository is null
                ? null
                : async ct =>
                {
                    var ctrl = GetRunControl(registryKey);
                    if (ctrl is not null)
                        await ctrl.CheckpointAsync(ct).ConfigureAwait(false);
                },
            UpdateTrace = runRepository is null
                ? null
                : updater =>
                {
                    var accumulator = new Agentivity.Infrastructure.Core.Diagnostics.DebugTraceAccumulator(GetStepTrace(registryKey, stepId));
                    updater(accumulator);
                    SetStepTrace(registryKey, stepId, accumulator.Build());
                },
        };

        var persistSession = ReadBool(aiNodeParams, "persist_session") ?? true;
        var systemPrompt = ReadString(aiNodeParams, "system_prompt") ?? string.Empty;
        var runtimeArguments = new Dictionary<string, global::Agentivity.Domain.Arguments.ArgumentDefinition>(StringComparer.OrdinalIgnoreCase);
        var images = ReadImages(args);

        return new ResolvedAgentDefinition(modeRuntime, runtimeArguments, runtimeParameters, resolvedPolicies, resolvedGuardrails, memoryContext, resolvedPrompt, systemPrompt, persistSession, runtimePort, contextServices, images);
    }

    /// <summary>
    /// Reads a per-turn image list from <c>args["images"]</c>. Populated by
    /// <see cref="Agentivity.Host.Executions.ExecutionService"/> for a live client-initiated
    /// execution (a plain in-process <c>Dictionary&lt;string, object?&gt;</c>, so the value
    /// arrives as the exact CLR type put there — not JSON-deserialized at this layer) — tolerant
    /// of arriving as something else too, so a future caller building <c>args</c> differently
    /// doesn't silently drop images.
    /// </summary>
    private static IReadOnlyList<Agentivity.Infrastructure.Core.Llm.ImageAttachment>? ReadImages(IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("images", out var raw) || raw is null)
            return null;

        if (raw is IReadOnlyList<Agentivity.Infrastructure.Core.Llm.ImageAttachment> list)
            return list.Count > 0 ? list : null;

        if (raw is IEnumerable<Agentivity.Infrastructure.Core.Llm.ImageAttachment> enumerable)
        {
            var materialized = enumerable.ToList();
            return materialized.Count > 0 ? materialized : null;
        }

        return null;
    }

    private static string ResolvePrompt(JsonElement? aiNodeParams, IReadOnlyDictionary<string, object?> args)
    {
        var state = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        var promptTemplate = ReadString(aiNodeParams, "prompt");
        return !string.IsNullOrWhiteSpace(promptTemplate) ? ArgumentValueResolver.Resolve(promptTemplate, state) as string ?? promptTemplate
            : args.TryGetValue("input", out var inp) && inp is string inpStr ? inpStr
            : string.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ReadString(JsonElement? el, string key)
    {
        if (el is null)
            return null;
        return el.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static bool? ReadBool(JsonElement? el, string key)
    {
        if (el is null)
            return null;
        if (!el.Value.TryGetProperty(key, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.True ? true
            : prop.ValueKind == JsonValueKind.False ? false
            : null;
    }

    private static double? ReadDouble(JsonElement? el, string key)
    {
        if (el is null)
            return null;
        return el.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetDouble() : null;
    }

    /// <summary>
    /// Reads a whole-number JSON value as <see cref="int"/>. Deliberately tolerant of a
    /// trailing ".0" (e.g. <c>10000.0</c>) — <see cref="JsonElement.GetInt32"/> throws
    /// <see cref="FormatException"/> for any JSON number token containing a decimal point,
    /// even a whole one, and several UI number inputs (e.g. Flutter's <c>double</c>-typed
    /// fields) serialize round, user-entered integers this way. <see cref="JsonElement.TryGetInt32"/>
    /// covers the clean-integer case without throwing; the <see cref="JsonElement.TryGetDouble"/>
    /// fallback covers the "looks like a float, is actually whole" case instead of failing the
    /// whole node's execution on what the user experiences as an ordinary integer field edit.
    /// </summary>
    private static int? ReadInt(JsonElement? el, string key)
    {
        if (el is null)
            return null;
        if (!el.Value.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return null;
        if (prop.TryGetInt32(out var i))
            return i;
        return prop.TryGetDouble(out var d) && d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
    }
}
