using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Interactions.Forms;

/// <summary>
/// All forms channel logic: HIL gate dispatch, thread creation, agent tool capabilities.
/// Uses <see cref="IInteractionThreadStore"/> (generic, shared with Chat) for persistence
/// and <see cref="IInteractionRequestStore"/> for HIL request tracking.
/// Consumed by FormsChannelNode (thin shell in Catalog).
/// </summary>
public sealed class FormsChannel : IInteractionChannel, IHumanInteractionChannel, IAgentHumanLoopCapabilityProvider
{
    private const string RequestHumanFormToolName = "request_human_form";
    private static readonly HumanInteractionOperation[] SupportedOps = [HumanInteractionOperation.SendAndWait, HumanInteractionOperation.Receive];

    private readonly IInteractionThreadStore? _threadStore;
    private readonly IInteractionRequestStore? _requestStore;
    private readonly IRunEventBus? _eventBus;

    public string ChannelType => "forms";

    public FormsChannel(IInteractionThreadStore? threadStore = null, IInteractionRequestStore? requestStore = null, IRunEventBus? eventBus = null)
    {
        _threadStore = threadStore;
        _requestStore = requestStore;
        _eventBus = eventBus;
    }

    public IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations() => SupportedOps;

    // This channel's one tool is inherently a request-and-wait (SendAndWait) — there is no
    // restricted "send only" shape to fall back to, unlike ChatChannel's interact_with_human.
    // When the requesting agent isn't allowed to wait for a reply at all, offer no tool here
    // rather than one that only pretends not to block.
    public AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true) =>
        !allowsAskMode
            ? AgentHumanLoopCapabilities.None
            : new(
            [
                AgentHumanLoopToolDefinitionJson.Create(
                    name: RequestHumanFormToolName,
                    description: "Request structured information from a human by defining a concise form with only the fields you need. Use an empty fields array when you only need to show information and wait for a simple acknowledgment.",
                    inputSchema: """
                    {
                      "type": "object",
                      "properties": {
                        "title": { "type": "string" },
                        "questions_to_ask": {
                          "type": "array",
                          "minItems": 0,
                          "items": { "type": "string" }
                        },
                        "description": { "type": "string" },
                        "fields": {
                          "type": "array",
                          "minItems": 0,
                          "items": {
                            "type": "object",
                            "properties": {
                              "name": { "type": "string" },
                              "label": { "type": "string" },
                              "type": { "type": "string", "enum": ["Text", "MultilineText", "Number", "Boolean", "Choice", "Date", "File"] },
                              "required": { "type": "boolean" },
                              "hint": { "type": "string" },
                              "defaultValue": { "description": "Pre-filled value shown in the field. For Number fields use a plain number (e.g. 7, not '7%'). For Boolean use true/false. For Choice use one of the options." },
                              "options": { "type": "array", "items": { "type": "string" } }
                            },
                            "required": ["name", "label", "type"],
                            "additionalProperties": false
                          }
                        }
                      },
                      "required": ["title", "questions_to_ask", "fields"],
                      "additionalProperties": false
                    }
                    """,
                    interactionKind: InteractionKind.Input,
                    operation: HumanInteractionOperation.SendAndWait,
                    resultMode: AgentHumanLoopResultMode.Json,
                    defaultTitle: "Additional information required",
                    questionsToAskArgumentName: "questions_to_ask",
                    titleArgumentName: "title",
                    descriptionArgumentName: "description",
                    fieldsArgumentName: "fields"
                ),
            ],
            [
                "A structured human-loop forms channel is connected.",
                $"`{RequestHumanFormToolName}` is the channel-native way to request structured human input in this channel.",
                "Always pre-fill `defaultValue` for any field whose value you already know or can infer from the conversation — the user should only need to confirm, not re-type known information.",
                "For Number fields, `defaultValue` must be a plain number (e.g. 7, not '7%' or '7 percent').",
                $"When you need human input, call `{RequestHumanFormToolName}` instead of asking follow-up questions in normal assistant text.",
                "Always include `questions_to_ask` with the exact human-facing question list generated for this request.",
                "Define only the fields that the human must complete.",
                "If no structured input is required, you may send an informational form with an empty fields array so the user only needs to acknowledge or close it.",
                "Prefer explicit field definitions over free-text follow-up questions when using this channel.",
            ]
        );

    /// <summary>
    /// Called by BaseInteractionNode (workflow FormInteractionNode) via InteractionChannelDispatcher.
    /// Creates a forms thread (same model as chat thread) and emits hil_gate_reached.
    /// The request itself is saved by BaseInteractionNode._store.
    /// </summary>
    public async ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        var thread = await ResolveThreadAsync(request, cancellationToken).ConfigureAwait(false);
        PublishHilGate(request, thread?.Id);
        return ValueTask.FromResult(new InteractionChannelDispatchResult(true, BuildMetadata(channel, request, thread))).Result;
    }

    /// <summary>
    /// Called when an agent uses the <c>request_human_form</c> tool.
    /// Stores the request, creates a thread, and emits hil_gate_reached.
    /// </summary>
    public async ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        if (_requestStore is not null)
            await _requestStore.SaveRequestAsync(request, cancellationToken).ConfigureAwait(false);

        var thread = await ResolveThreadAsync(request, cancellationToken).ConfigureAwait(false);
        PublishHilGate(request, thread?.Id);

        return HumanInteractionExecutionResult.Waiting(BuildMetadata(channel, request, thread));
    }

    public ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default) => ValueTask.FromResult<InteractionResponse?>(null);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async ValueTask<InteractionThread?> ResolveThreadAsync(InteractionRequest request, CancellationToken ct)
    {
        if (_threadStore is null)
            return null;
        return await _threadStore.GetOrCreateDefaultThreadAsync(request.RunId, request.ExecutionId, cancellationToken: ct).ConfigureAwait(false);
    }

    private void PublishHilGate(InteractionRequest request, Guid? threadId) =>
        _eventBus?.Publish(
            new RunEvent(
                RunId: request.RunId.ToString("D"),
                DefinitionId: "forms",
                RunKind: RunKind.Unknown,
                Kind: RunEventKind.hil_gate_reached,
                EventName: "forms_hil_gate_reached",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Payload: new
                {
                    requestId = request.Id,
                    title = request.Title,
                    channelType = "forms",
                    threadId = threadId?.ToString(),
                },
                Terminal: false,
                ExecutionId: request.ExecutionId
            )
        );

    private static Dictionary<string, object?> BuildMetadata(ChannelBinding channel, InteractionRequest request, InteractionThread? thread) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["forms.request_id"] = request.Id.ToString(),
            ["forms.respondent"] = channel.Respondent,
            ["forms.thread_id"] = thread?.Id.ToString(),
        };
}
