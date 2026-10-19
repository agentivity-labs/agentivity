using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Fabric;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Llm;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Agents;

/// <summary>
/// Multi-step AI agent node with native LLM tool calling.
/// Keeps agent tools strictly separated from other tool families such as content tools.
/// </summary>
public sealed partial class AiNode : BaseAINode, IWorkflowNodeReconfigurable
{
    private readonly AgentHost _agentEngineProxy;
    private readonly InteractionStore _interactionStore;
    private readonly IRunEventBus? _eventBus;

    public AiNode(ILlmClientFactory llmClientFactory, InteractionStore interactionStore, AgentHost agentEngineProxy, ILogger<AiNode> logger, IRunEventBus? eventBus = null)
        : base("ai.node", llmClientFactory, logger)
    {
        _agentEngineProxy = agentEngineProxy ?? throw new ArgumentNullException(nameof(agentEngineProxy));
        _interactionStore = interactionStore ?? throw new ArgumentNullException(nameof(interactionStore));
        _eventBus = eventBus;

        DefineMetadata("AI Agent", "Runs a multi-step LLM agent with native tool calling, and searches the Knowledge Bases connected to it.", "ai.agents", whenToUse: "To have an AI work through a task step by step in this workflow, with the model, tools, knowledge and people you plug into it. Not for a single question and answer: use LLM Prompt instead; to reuse an agent saved in your library, use Agent.");
        DefineEffects(NodeEffects.Costs | NodeEffects.Delegates);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1, color: "#FF607D8B");
        DefineOutputPort("out", "Out", "Default output port");

        DefineResourceInputPort("llm_model", "LLM Model", ResourcePortCategory.LlmModel, "LLM provider and model configuration", color: "#FF9AA0D6");
        DefineResourceInputPort("knowledge", "Knowledge Bases", ResourcePortCategory.KnowledgeSource, "The Knowledge Bases the agent searches, each with the Knowledge Base step (optional)", maxConnections: -1, isRequired: false, color: "#FF9EBC99");
        DefineResourceInputPort("channel", "Human Loop Channel", ResourcePortCategory.InteractionChannel, "Human interaction channel used when the agent needs human input (optional)", isRequired: false, color: "#FFD7C07C");
        DefineResourceInputPort("session", "Agent Session", ResourcePortCategory.AIAgentSession, "Persistent conversation session store (optional)", isRequired: false, color: "#FFD9A79F");
        DefineResourceInputPort("tools", "AI Agent Tools", ResourcePortCategory.AIAgentTool, "LLM-callable AI agent tools", maxConnections: -1, isRequired: false, color: "#FFAFBCC1");
        DefineResourceInputPort("policies", "AI Agent Policies", ResourcePortCategory.AIAgentPolicy, "Composable policy layers applied to the agent runtime (optional)", maxConnections: -1, isRequired: false, color: "#FFB5A7CF");
        DefineResourceInputPort("guardrails", "AI Agent Guardrails", ResourcePortCategory.AIAgentGuardrail, "Composable runtime guardrails applied to selected agent surfaces (optional)", maxConnections: -1, isRequired: false, color: "#FFD9B09F");

        var supportedAgentModes = _agentEngineProxy.ModeRuntimes.Select(modeRuntime => (modeRuntime.ModeId, modeRuntime.DisplayName)).ToList();
        string defaultAgentMode = supportedAgentModes.FirstOrDefault().ModeId ?? throw new InvalidOperationException("The configured agent engine does not expose any runtime mode.");

        DefineChoice(
            "agent_mode",
            supportedAgentModes,
            "Agent execution mode",
            defaultAgentMode,
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompting",
                Order = 5,
            },
            triggersReconfigure: true,
            required: false
        );

        DefineInput(
            "system_prompt",
            ArgumentType.Text,
            "System instructions (optional)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompting",
                Order = 20,
            },
            required: false
        );
        DefineInput(
            "prompt",
            ArgumentType.Text,
            "User prompt (required)",
            "{{input}}",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompting",
                Order = 10,
            },
            required: true
        );
        DefineInput(
            "images",
            ArgumentType.ImageRef,
            "Optional image to attach to the initial prompt — only used by vision-capable models (GPT-4o, Claude, Gemini). Ignored by text-only models.",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Prompting",
                Order = 15,
            },
            required: false
        );

        DefineInput(
            "temperature",
            ArgumentType.Number,
            "Creativity (0-2)",
            0.2,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Sampling",
                Order = 10,
            },
            required: false
        );
        DefineInput(
            "max_iterations",
            ArgumentType.Number,
            "Maximum number of rounds — one call to the model, then the tools it asked for (leave empty for no limit)",
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Limits",
                Order = 30,
            },
            required: false
        );
        DefineInput(
            "response_quality_threshold",
            ArgumentType.Number,
            "Minimum response quality score before finalization (0 disables the quality pass)",
            0,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Response",
                Order = 35,
                Min = 0,
                Max = 10,
                Step = 0.5,
            },
            required: false
        );

        // Mode-specific runtime parameters — registered via OnReconfigure for the default mode.
        // When agent_mode changes in the editor, OnReconfigure is called by the reconfigure endpoint.
        OnReconfigure(null!, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["agent_mode"] = defaultAgentMode });

        DefineChoice(
            "tool_choice_mode",
            [("auto", "Auto"), ("required", "Required"), ("none", "None")],
            "Tool usage strategy",
            "auto",
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Tools",
                Order = 30,
            },
            required: false
        );
        DefineInput(
            "allow_parallel_tool_calls",
            ArgumentType.Boolean,
            "Allow the model to ask for several tools in the same round",
            false,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Tools",
                Order = 40,
            },
            required: false
        );
        DefineInput(
            "response_schema",
            ArgumentType.Text,
            "JSON Schema for the final response (optional)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Response",
                Order = 50,
            },
            required: false
        );
        DefineInput(
            "allows_human_questions",
            ArgumentType.Boolean,
            "Allow this agent to ask the user a question and wait for a reply. Turn off to guarantee it always delivers its final answer directly instead of pausing the run.",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Advanced",
                Group = "Interaction",
                Order = 45,
            },
            required: false
        );
        DefineInput(
            "session_id",
            ArgumentType.Text,
            "Session identifier (leave empty to create a new one when a session resource is connected)",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "Session Binding",
                Group = "Identity",
                Order = 10,
            },
            required: false
        );
        DefineInput(
            "session_scope_hint",
            ArgumentType.Text,
            "Optional scope hint used by forked session resources",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "Session Binding",
                Group = "Identity",
                Order = 20,
            },
            required: false
        );
        DefineInput(
            "restore_session",
            ArgumentType.Boolean,
            "Restore previous conversation history from the connected session resource",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Session Binding",
                Group = "Persistence",
                Order = 30,
            },
            required: false
        );
        DefineInput(
            "persist_session",
            ArgumentType.Boolean,
            "Persist the conversation history to the connected session resource",
            true,
            ui: new ArgumentUiMetadata
            {
                Tab = "Session Binding",
                Group = "Persistence",
                Order = 40,
            },
            required: false
        );

        DefineOutput("response", ArgumentType.Text, "Final agent response");
        DefineOutput("finish_reason", ArgumentType.Text, "Final LLM finish reason");
        DefineOutput("iterations", ArgumentType.Number, "Number of rounds the agent took");
        DefineOutput("tool_calls", ArgumentType.Text, "Executed tool calls as JSON");
        DefineOutput("guardrail_warnings", ArgumentType.Text, "JSON array of non-blocking guardrail matches (guardrail_id, stage, message) — branch on this with an If/Switch to decide what to do (empty array when nothing matched)");
        DefineOutput("session_id", ArgumentType.Text, "Resolved session identifier when a session resource is connected");
        DefineOutput("session_scope", ArgumentType.Text, "Resolved session scope");
        DefineOutput("session_restored", ArgumentType.Boolean, "Whether an existing session history was restored");
        DefineOutput("session_message_count", ArgumentType.Number, "Number of conversation messages currently stored in the session");

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    /// <inheritdoc />
    public void OnReconfigure(WorkflowNodeDefinition currentNode, IReadOnlyDictionary<string, object?> currentValues)
    {
        // Remove all previously registered mode-specific runtime parameter inputs.
        var allRuntimeParamKeys = _agentEngineProxy.ModeRuntimes.SelectMany(r => r.RuntimeArguments.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveInputsWhere(key => allRuntimeParamKeys.Contains(key));

        // Resolve the selected mode and register its parameters under its own UI tab.
        string? modeId = currentValues.TryGetValue("agent_mode", out object? m) ? m?.ToString() : null;
        string defaultModeId = _agentEngineProxy.ModeRuntimes.First().ModeId;
        IAgenticModeRuntime runtime = _agentEngineProxy.ResolveRuntime(modeId ?? defaultModeId).ModeRuntime;

        foreach (string key in runtime.RuntimeArguments.Keys)
        {
            ArgumentDefinition value = runtime.RuntimeArguments[key];
            InjectInput(key, value);
        }
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken ct = default)
    {
        ArgumentInputValues args = GetInputs(config);

        ExecutionCorrelationContext agentCorrelation = BuildAgentCorrelationContext(config, services);

        try
        {
            bool restoreSession = args.GetBoolean("restore_session") ?? true;
            bool persistSession = args.GetBoolean("persist_session") ?? true;

            // ── Pre-run preparation ───────────────────────────────────────────
            PreparedAgentHumanLoopContext humanLoopContext = WorkflowHilProxy.PrepareHumanLoopContext(config, services);

            // ── Context-specific proxies ──────────────────────────────────────
            var sessionProxy = new WorkflowSessionProxy(config, services, args.GetText("session_id"), args.GetText("session_scope_hint"), restoreSession, persistSession);
            var toolsProxy = new WorkflowToolsProxy(config, services);
            var hilProxy = new WorkflowHilProxy(humanLoopContext, await sessionProxy.LoadAsync(ct).ConfigureAwait(false), config, services, _interactionStore);
            var traceProxy = new WorkflowTraceProxy(config, services);

            var resolver = new DelegateAgentDefinitionResolver((_, _, resolveCt) => ResolveDefinitionAsync(args, config, services, _agentEngineProxy, agentCorrelation, resolveCt));
            var agentExecutable = new AgentExecutable(agentDefinition: null, _agentEngineProxy, telemetryEmitter: services.TelemetryEmitter);

            var runContext = RunContext.FromCorrelationContext(agentCorrelation, RunKind.Workflow);
            var genericArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = args.GetText("prompt") };

            // ── Unified pipeline (resolve + run + persist) ─────────────────────
            AgentRunResult result = await agentExecutable.ExecuteAgentAsync(genericArgs, runContext, toolsProxy, sessionProxy, hilProxy, traceProxy, humanLoopContext, resolver, ct).ConfigureAwait(false);

            SessionContext sessionContext = await sessionProxy.LoadAsync(ct).ConfigureAwait(false);

            // ── Completion (node outputs) ───────────────────────────────────────
            var executedToolCallsJson = System.Text.Json.JsonSerializer.Serialize(result.ExecutedToolCalls ?? [], AgentJsonSerializer.Options);
            // Guardrail matches are DATA, not a built-in action — a Warn match never does anything
            // on its own beyond appearing here; it's up to the workflow to branch on this (an
            // If/Switch downstream) and decide what "warned" should actually mean: log it, retry
            // with a different prompt, route to human review, or just continue. See EPIC-0462 P1-8.
            var guardrailWarningsJson = System.Text.Json.JsonSerializer.Serialize(result.GuardrailWarnings, AgentJsonSerializer.Options);

            return NodeResult
                .Create()
                .AddText("response", result.Response)
                .AddText("finish_reason", result.FinishReason)
                .AddNumber("iterations", result.ExecutedIterations)
                .AddText("tool_calls", executedToolCallsJson)
                .AddText("guardrail_warnings", guardrailWarningsJson)
                .AddText("session_id", sessionContext.SessionId)
                .AddText("session_scope", sessionContext.Scope)
                .AddBoolean("session_restored", sessionContext.Restored)
                .AddNumber("session_message_count", result.PersistedMessageCount);
        }
        catch (Exception ex)
        {
            services.PublishStatus($"AI Node execution failed: {ex.Message}");
            throw;
        }
    }

    private static ExecutionCorrelationContext BuildAgentCorrelationContext(NodeConfig config, WorkflowRunServices services)
    {
        if (config.State.TryGetValue(AgentRuntimeKeys.AgentRunIdStateKey, out var rawAgentRunId) && rawAgentRunId is string existingAgentRunId && !string.IsNullOrWhiteSpace(existingAgentRunId))
        {
            return BuildCorrelation(config, services, existingAgentRunId);
        }

        var agentRunId = $"agent-{Guid.NewGuid():N}";
        config.State[AgentRuntimeKeys.AgentRunIdStateKey] = agentRunId;
        return BuildCorrelation(config, services, agentRunId);

        static ExecutionCorrelationContext BuildCorrelation(NodeConfig config, WorkflowRunServices services, string agentRunId)
        {
            var parent = services.CorrelationContext;
            return new ExecutionCorrelationContext(RunId: services.RunId, EntityId: parent?.EntityId ?? config.Definition.Id, TraceId: parent?.TraceId ?? services.RunId.ToString("N"), SpanId: agentRunId, ParentSpanId: parent?.SpanId, NodeRunId: agentRunId, NodeExecutionId: config.Node.Id);
        }
    }

    /// <summary>
    /// Resolves all upstream values (mode, runtime parameters, policies, guardrails, memory, prompt,
    /// context services) needed by <see cref="AgentHost.RunAgentAsync"/> from this node's <see cref="NodeConfig"/>.
    /// Wrapped as an <see cref="IAgentDefinitionResolver"/> via <see cref="DelegateAgentDefinitionResolver"/>.
    /// </summary>
    private static async ValueTask<ResolvedAgentDefinition> ResolveDefinitionAsync(ArgumentInputValues args, NodeConfig config, WorkflowRunServices services, AgentHost agentHost, ExecutionCorrelationContext agentCorrelation, CancellationToken ct)
    {
        string systemPrompt = args.GetText("system_prompt") ?? string.Empty;
        string prompt = args.RequireText("prompt");
        string agentModeInput = args.GetText("agent_mode") ?? agentHost.ModeRuntimes.First().ModeId;
        ResolvedAgenticEngineRuntime resolvedAgenticRuntime = agentHost.ResolveRuntime(agentModeInput);
        IAgenticModeRuntime resolvedModeRuntime = resolvedAgenticRuntime.ModeRuntime;

        // Runtime knobs — resolved via shared normalization (AgentRuntimeParameterResolver)
        // so workflow and standalone paths are always characterized identically.
        AgentRuntimeParameters runtimeParameters = AgentRuntimeParameterResolver.Resolve(new AgentParameterReader(args.GetText, args.GetDouble, args.GetInt, args.GetBoolean), resolvedModeRuntime.Capabilities.AllowsToolChoiceConfiguration);

        bool persistSession = args.GetBoolean("persist_session") ?? true;

        // ── Policies + guardrails ─────────────────────────────────────────────
        ResolvedAgentPolicies resolvedPolicies = new AgentPolicyResolver().Resolve(AgentPolicyLayerFactory.BuildPolicyLayers(resolvedModeRuntime, AgentPolicyResourceResolver.ResolveConnectedPolicyLayers(config)));
        IReadOnlyList<AgentGuardrailDefinition> resolvedGuardrails = new AgentGuardrailResolver().Resolve(config);

        // Mode-specific runtime arguments resolved from node config values.
        var runtimeArguments = resolvedModeRuntime.RuntimeArguments.Values.ToDictionary(
            p => p.Name,
            p =>
            {
                p.Value = config.Resolve(p.Name) ?? p.DefaultValue;
                return p;
            }
        );

        var runtimePort = new WorkflowAgentLlmProxy(config, services);

        List<ImageAttachment> images = await ImageAttachmentResolver.ResolveAsync(config.Resolve("images"), ct).ConfigureAwait(false);

        // ── Context services (workflow-specific delegates) ────────────────────
        var contextServices = new AgentRunServices
        {
            RunId = services.RunId,
            CorrelationContext = agentCorrelation,
            TelemetryEmitter = services.TelemetryEmitter,
            Checkpoint = innerCt => services.CheckpointAsync(innerCt),
            SetState = (key, val) => config.State[key] = val,
            GetState = key => config.State.TryGetValue(key, out var v) ? (true, v) : (false, null),
            UpdateTrace = updater => services.UpdateTrace(updater),
            PublishStatus = msg => services.PublishStatus(msg),
            LogDebug = msg => services.LogDebug(msg),
        };

        return new ResolvedAgentDefinition(resolvedModeRuntime, runtimeArguments, runtimeParameters, resolvedPolicies, resolvedGuardrails, prompt, systemPrompt, persistSession, runtimePort, contextServices, images.Count > 0 ? images : null);
    }
}
