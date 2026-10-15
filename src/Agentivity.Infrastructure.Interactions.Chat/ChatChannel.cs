using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflow.AgUi;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Interactions.Chat;

/// <summary>
/// All chat channel logic: thread resolution, message dispatch, HIL gate events,
/// inbound resolution, AG-UI capabilities. Consumed by ChatChannelNode (thin shell).
/// </summary>
public sealed class ChatChannel : IInteractionChannel, IHumanInteractionChannel, IAgentHumanLoopCapabilityProvider, IAgUiCapabilityProvider
{
    private static readonly HumanInteractionOperation[] SupportedOps = [HumanInteractionOperation.Send, HumanInteractionOperation.SendAndWait, HumanInteractionOperation.Receive, HumanInteractionOperation.Update, HumanInteractionOperation.Delete, HumanInteractionOperation.Search];
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IInteractionThreadStore _store;
    private readonly IRunEventBus? _eventBus;
    private readonly IExecutionRecordStore? _executionStore;
    private readonly IAgUiWidgetBundleRegistry? _widgetBundleRegistry;
    private readonly ILogger? _logger;

    public string ChannelType => "chat";

    private const string InteractToolName = "interact_with_human";

    private readonly IReadOnlyList<ChatChannelWidget> _availableWidgets;

    public ChatChannel(IInteractionThreadStore store, IRunEventBus? eventBus = null, IExecutionRecordStore? executionStore = null, IReadOnlyList<ChatChannelWidget>? availableWidgets = null, IAgUiWidgetBundleRegistry? widgetBundleRegistry = null, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eventBus = eventBus;
        _executionStore = executionStore;
        _availableWidgets = availableWidgets ?? [];
        _widgetBundleRegistry = widgetBundleRegistry;
        _logger = logger;
    }

    public IReadOnlyCollection<HumanInteractionOperation> GetSupportedOperations() => SupportedOps;

    /// <summary>
    /// Exposes a single generic <c>interact_with_human</c> tool: <c>mode</c> ("send" just
    /// delivers content and continues, "ask" also waits for a human reply) plus <c>content</c>,
    /// an ordered list of blocks mixing free text and any active interaction widget (each widget
    /// block's shape is exactly its Flutter props — no translation layer). This keeps content
    /// generation (which blocks, which widgets) and interaction control (send vs. wait) as one
    /// tool call, but as two independent concerns within it, rather than one tool per widget.
    /// Plain free text with no tool call at all is instead caught by the
    /// <c>MissedAskHumanFallback</c> safety net (see <c>AgentSchemas</c>), unaffected by this.
    /// </summary>
    public AgentHumanLoopCapabilities GetAgentHumanLoopCapabilities(ChannelBinding channel, bool allowsAskMode = true)
    {
        // Widgets like QuestionForm/ChoiceCard/ConfirmCard exist to solicit a reply — offering them
        // to an agent that will never wait for one (mode is restricted to "send" below) would let it
        // render what looks like a question with no way for the human's answer to come back.
        var interactionWidgets = allowsAskMode ? ResolveInteractionWidgets(channel.Properties) : [];

        var description = allowsAskMode
            ? "Deliver content to the human in this chat, optionally waiting for their reply. Use 'ask' mode whenever you need an answer before continuing; use 'send' to just inform them and keep going."
            : "Deliver content to the human in this chat. This agent may only send — it can never wait for a reply.";

        var toolDefinitions = new List<JsonObject>
        {
            AgentHumanLoopToolDefinitionJson.Create(
                name: InteractToolName,
                description: description,
                inputSchema: BuildInteractToolSchema(interactionWidgets, allowsAskMode).ToJsonString(JsonOpts),
                interactionKind: InteractionKind.Input,
                operation: HumanInteractionOperation.SendAndWait,
                resultMode: AgentHumanLoopResultMode.FreeText,
                defaultTitle: "Human interaction",
                contentBlocksArgumentName: "content",
                modeArgumentName: "mode"
            ),
        };

        return new AgentHumanLoopCapabilities(toolDefinitions, BuildSystemPromptLines(interactionWidgets, allowsAskMode));
    }

    /// <summary>
    /// Every widget the agent may pick as an <c>interact_with_human</c> content block — platform
    /// bundle widgets (ChoiceCard, ConfirmCard, …) plus this channel's own <c>custom_widgets</c>
    /// (app-declared, zero-backend-change). Both sources land in the exact same list, so a custom
    /// widget gets the identical pause/resume/rendering treatment as a built-in one, for every
    /// entity kind (standalone agent, team member, workflow) — this method is the one place the
    /// tool schema is built (<see cref="GetAgentHumanLoopCapabilities"/>), shared by all of them.
    /// </summary>
    private IReadOnlyList<AgUiWidgetDescriptor> ResolveInteractionWidgets(IResourceBindingReader binding)
    {
        var bundleWidgets = _widgetBundleRegistry is null
            ? []
            : GetActiveBundleIds(binding)
                .Select(id => _widgetBundleRegistry.GetById(id))
                .Where(bundle => bundle is not null)
                .SelectMany(bundle => bundle!.Widgets)
                .Where(widget => widget.RequiresHumanResponse && !string.IsNullOrWhiteSpace(widget.ParametersSchema));

        // Custom widgets are declared purely to be shown to a human and get a reply, so they're
        // always treated as RequiresHumanResponse — there's no "send only" custom widget concept.
        var customWidgets = GetCustomWidgets(binding)
            .Where(definition => !string.IsNullOrWhiteSpace(definition.ParametersSchema))
            .Select(definition => new AgUiWidgetDescriptor(definition.Name, "custom", definition.Description, definition.ParametersSchema!, RequiresHumanResponse: true));

        return bundleWidgets
            .Concat(customWidgets)
            .GroupBy(widget => widget.TypeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()) // a custom widget reusing a bundle widget's name loses silently to the bundle one, rather than producing two oneOf branches with the same discriminator
            .ToList();
    }

    private static JsonObject BuildInteractToolSchema(IReadOnlyList<AgUiWidgetDescriptor> interactionWidgets, bool allowsAskMode = true)
    {
        var contentVariants = new JsonArray { BuildTextBlockSchema() };
        foreach (var widget in interactionWidgets)
            contentVariants.Add(BuildWidgetBlockSchema(widget.TypeKey, widget.ParametersSchema!));

        var modeSchema = allowsAskMode
            ? new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "send", "ask" },
                ["description"] = "'send' delivers content and continues immediately. 'ask' delivers content and waits for a human reply before continuing.",
            }
            : new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "send" },
                ["description"] = "'send' is the only choice for this agent — it delivers content and continues immediately; it can never wait for a human reply.",
            };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mode"] = modeSchema,
                ["content"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["description"] = "Each block in this array has a fixed shape: only the fields its type declares below. Check that shape before deciding where to put something you need to say — some block types have a field meant for a sentence or a question (e.g. ConfirmCard's 'message', ChoiceCard's 'question'), others don't (e.g. Timeline only has short day titles, no field for a question). When none of the block types you're using have a field for what you need to say, add another block — whichever type actually has a field suited for it — to say it explicitly; never assume a block can carry something its own fields don't list.",
                    ["items"] = new JsonObject { ["oneOf"] = contentVariants },
                },
            },
            ["required"] = new JsonArray { "mode", "content" },
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject BuildTextBlockSchema() =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["const"] = "text" },
                ["text"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray { "type", "text" },
            ["additionalProperties"] = false,
        };

    /// <summary>
    /// Nests a widget's own props schema (authored in <see cref="AgUiWidgetCatalog"/>, identical
    /// to what the Flutter widget expects) as one <c>oneOf</c> branch of a content block, adding
    /// only the discriminator <c>type</c> property — no other translation.
    /// </summary>
    private static JsonObject BuildWidgetBlockSchema(string typeKey, string widgetSchemaJson)
    {
        var widgetSchema = JsonNode.Parse(widgetSchemaJson)!.AsObject();
        var properties = widgetSchema["properties"] is JsonObject existingProperties ? (JsonObject)existingProperties.DeepClone() : [];
        properties.Insert(0, "type", new JsonObject { ["const"] = typeKey });

        var required = new JsonArray { "type" };
        if (widgetSchema["required"] is JsonArray existingRequired)
            foreach (var value in existingRequired)
                required.Add(value?.DeepClone());

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private IReadOnlyList<string> BuildSystemPromptLines(IReadOnlyList<AgUiWidgetDescriptor> interactionWidgets, bool allowsAskMode = true)
    {
        var lines = new List<string>
        {
            "A free-text internal chat channel is connected.",
            allowsAskMode
                ? $"`{InteractToolName}` is the channel-native way to send content to the human or to ask them something."
                : $"`{InteractToolName}` is the channel-native way to send content to the human.",
        };

        if (allowsAskMode)
        {
            lines.Add($"When you need human input, call `{InteractToolName}` with `mode: \"ask\"` instead of asking follow-up questions in plain assistant text.");
            lines.Add("HARD RULE, no exceptions: an `ask` call's `content` may contain AT MOST ONE item that asks the human for something — one widget, or one plain-text question if no widget fits. NEVER two widgets. NEVER a widget plus a text question. A single `ask` only ever gets one reply back, so anything beyond the first widget/question is silently ignored by the human — asking it there wastes it. Need to ask several things? Put them all as fields in that one widget, or send this one now and ask the rest in your NEXT `ask` call after this reply comes back.");
        }
        else
        {
            lines.Add("This agent must never ask the human a question or wait for a reply — it may only deliver content and continue. If information seems missing, make the most reasonable assumption, state it plainly, and proceed instead of asking.");
        }

        lines.Add("`content` is an ordered list of blocks — mix free text and widgets in whatever order makes sense; do not restate a widget's content as a separate text block.");
        lines.Add($"Your entire visible reply — all text and any widgets — must be inside this `content` array. Never write your answer as a separate message alongside this call: text you place outside `content` is never shown to the human.");
        lines.Add($"Call `{InteractToolName}` with `mode: \"send\"` at most once per turn, and never to repeat something already delivered — once the conversation is done, end your turn with a short plain-text reply instead of another tool call.");

        if (interactionWidgets.Count > 0)
        {
            lines.Add("Available interaction widgets for `content` blocks:");
            foreach (var w in interactionWidgets)
                lines.Add($"  • `{w.TypeKey}` — {w.Description}");
            lines.Add(string.Empty);
            lines.Add("Prefer a widget over plain text whenever the question fits one of these shapes — do not phrase these as plain-text questions:");
            lines.Add("  • Yes/no or confirm-before-acting question (e.g. \"Voulez-vous que j'ajoute X ?\") → use `ConfirmCard`, not a text sentence ending in a question mark.");
            lines.Add("  • Choosing between a small fixed set of named options (e.g. \"portable ou de bureau ?\") → use `ChoiceCard`.");
            lines.Add("  • Several distinct pieces of information needed at once → use `QuestionForm` with one entry per piece of information, not several sentences.");
            lines.Add("  • A rating, a date, or a recap before confirming → use `RatingCard`, `DatePickerCard`, or `SummaryCard` respectively.");
            lines.Add("  • A document, file, web link or long pasted text (a CV, a contract, a report) → use `SourceInput`; the reply is a small JSON envelope (`{\"kind\":\"file\",\"fileId\":...}`, `{\"kind\":\"url\",\"url\":...}` or `{\"kind\":\"text\",\"content\":...}`) that you pass on unchanged, not prose.");
            lines.Add("A plain `text` block is only for framing/introducing the widget (e.g. a short \"Bonjour, précisons votre besoin :\") or for genuinely open-ended questions that no widget shape fits.");
            lines.Add("Example — WRONG: {\"type\":\"text\",\"text\":\"Voulez-vous aussi un clavier et une souris gaming ?\"}");
            lines.Add("Example — RIGHT: {\"type\":\"ConfirmCard\",\"title\":\"Accessoires\",\"message\":\"Voulez-vous aussi un clavier et une souris gaming ?\",\"confirmLabel\":\"Oui\",\"cancelLabel\":\"Non\"}");
            lines.Add("Still prefer a widget whenever one fits — but an `ask` call always resolves to exactly one human reply, so `content` must resolve every question through at most one item that asks for something. If everything you need right now fits one widget's fields, use exactly one widget for all of it (add fields rather than a second widget). Only when none of your questions fit any widget shape, ask them all in plain text instead, with no widget in the same call. Never pair a widget with a separate text question, and never include two widgets in the same call — anything you still need to ask afterward waits for a later `ask` call, once this one is answered.");
            lines.Add("Example — WRONG (widget + extra text question): [{\"type\":\"QuestionForm\", ...destinations/season fields...}, {\"type\":\"text\",\"text\":\"Et pour le budget ?\"}]");
            lines.Add("Example — WRONG (two widgets): [{\"type\":\"QuestionForm\", ...}, {\"type\":\"ChoiceCard\", ...}]");
            lines.Add("Example — RIGHT (one widget, all fields grouped): [{\"type\":\"QuestionForm\", ...destinations/season/budget fields...}]");
        }

        // Excludes any widget already listed above as an interaction widget: _availableWidgets is a
        // fixed catalog of every registered AG-UI widget, so without this filter the interaction
        // ones (QuestionForm, ChoiceCard, ConfirmCard, RatingCard, DatePickerCard) were being
        // printed twice in the same system prompt — once here, once above.
        var interactionTypeKeys = interactionWidgets.Select(w => w.TypeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visualOnlyWidgets = _availableWidgets.Where(w => !interactionTypeKeys.Contains(w.TypeKey)).ToList();
        if (visualOnlyWidgets.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("This channel also supports rich visual widgets outside of human interaction. Use them to make your responses clearer and more structured:");
            foreach (var w in visualOnlyWidgets)
                // The exact prop schema, not just the description — without it an agent guesses a
                // plausible-but-wrong shape (e.g. KeyValue's `items` as a flat object instead of an
                // array of {key,value} pairs) and renders an empty/broken widget.
                lines.Add($"  • `{w.TypeKey}` — {w.Description}. Data shape: {JsonNode.Parse(w.ParametersSchema)!.ToJsonString(JsonOpts)}");
            lines.Add("To render a widget, emit it as a CUSTOM event with the widget type as the event name and its data as the value, matching that widget's data shape exactly — no extra or missing fields.");
            lines.Add("You can freely mix text and widgets in any order within your response.");
        }

        return lines;
    }

    public async ValueTask<InteractionChannelDispatchResult> DispatchAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        var thread = await ResolveThreadAsync(channel, request, cancellationToken).ConfigureAwait(false);
        var metadata = CreateBaseMetadata(channel, request, thread);

        var blocks = TryGetContentBlocks(request);
        Guid messageId;
        string messageText;
        if (blocks is not null)
        {
            messageId = await PublishContentBlocksAsync(channel, request, thread, blocks, cancellationToken).ConfigureAwait(false);
            messageText = BuildInteractionText(request);
        }
        else
        {
            var (authorId, authorName) = ResolveAuthor(channel, request);
            var message = await _store
                .AddMessageAsync(
                    thread.Id,
                    new ThreadMessage
                    {
                        Id = Guid.NewGuid(),
                        ThreadId = thread.Id,
                        RunId = thread.RunId,
                        AuthorType = ThreadAuthorType.Workflow,
                        AuthorId = authorId,
                        AuthorName = authorName,
                        Text = BuildInteractionText(request),
                        Metadata = CreateMessageMetadata(request),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            messageId = message.Id;
            messageText = message.Text;
        }

        metadata["chat.message_id"] = messageId.ToString();
        metadata["chat.awaiting_response"] = true;

        PublishHilGate(request, thread.Id, messageId, messageText);

        return new InteractionChannelDispatchResult(true, metadata);
    }

    public async ValueTask<HumanInteractionExecutionResult> ExecuteAsync(ChannelBinding channel, InteractionRequest request, CancellationToken cancellationToken = default)
    {
        var thread = await ResolveThreadAsync(channel, request, cancellationToken).ConfigureAwait(false);
        var metadata = CreateBaseMetadata(channel, request, thread);

        switch (request.Operation)
        {
            case HumanInteractionOperation.Send:
            case HumanInteractionOperation.SendAndWait:
            case HumanInteractionOperation.Receive:
            {
                // Every HIL dispatch (explicit interact_with_human tool call, or the
                // MissedAskHumanFallback safety net flagging a plain-text reply) delivers its own
                // content via the block list / outbound text below. AgentExecutable no longer
                // publishes a separate "agent_response" chat event for turns that end in a HIL
                // suspension (AgentManifest.FinalizeAsync forces an empty response in that case),
                // so there is nothing to deduplicate against — storing here is the only place
                // this content ever reaches the chat.
                var messageId = Guid.NewGuid();
                var blocks = TryGetContentBlocks(request);
                Serilog.Log.Debug("[ChatDiag] ChatChannel.ExecuteAsync operation={Operation} blocksIsNull={BlocksIsNull} blocksCount={BlocksCount} messageLen={MessageLen} hasToolArgsMeta={HasToolArgsMeta}", request.Operation, blocks is null, blocks?.Count, request.Message?.Length, request.Metadata?.ContainsKey("human_review.tool_arguments_json"));
                if (blocks is not null)
                {
                    messageId = await PublishContentBlocksAsync(channel, request, thread, blocks, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var outboundText = BuildOutboundText(request);
                    Serilog.Log.Debug("[ChatDiag] ChatChannel fallback outboundTextLen={Len} preview={Preview}", outboundText?.Length, outboundText is { Length: > 0 } t ? t.Substring(0, Math.Min(120, t.Length)) : null);
                    if (!string.IsNullOrWhiteSpace(outboundText))
                    {
                        var (authorId, authorName) = ResolveAuthor(channel, request);
                        var stored = await _store
                            .AddMessageAsync(
                                thread.Id,
                                new ThreadMessage
                                {
                                    Id = messageId,
                                    ThreadId = thread.Id,
                                    RunId = thread.RunId,
                                    AuthorType = ThreadAuthorType.Workflow,
                                    AuthorId = authorId,
                                    AuthorName = authorName,
                                    Text = outboundText,
                                    Metadata = CreateMessageMetadata(request),
                                },
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        messageId = stored.Id;
                    }
                }

                metadata["chat.message_id"] = messageId.ToString();
                metadata["chat.awaiting_response"] = request.Operation is HumanInteractionOperation.SendAndWait or HumanInteractionOperation.Receive;

                if (request.Operation is HumanInteractionOperation.SendAndWait or HumanInteractionOperation.Receive)
                    PublishHilGate(request, thread.Id, messageId, BuildOutboundText(request));

                if (request.Operation is HumanInteractionOperation.SendAndWait or HumanInteractionOperation.Receive)
                    return HumanInteractionExecutionResult.Waiting(metadata);

                // Send-only: build a minimal completed response.
                var completedResponse = new InteractionResponse
                {
                    RequestId = request.Id,
                    Outcome = InteractionOutcome.Submitted,
                    Success = true,
                    ResponseText = BuildOutboundText(request),
                    MessageId = messageId.ToString(),
                    ThreadId = thread.Id.ToString(),
                    ConversationId = thread.Id.ToString(),
                    Channel = channel.ChannelType,
                    Metadata = metadata,
                };
                return HumanInteractionExecutionResult.Completed(completedResponse, metadata);
            }

            case HumanInteractionOperation.Update:
            {
                var messageId = ParseMessageId(request.MessageId, "Chat update requires a message id.");
                var message = await _store.UpdateMessageAsync(thread.Id, messageId, request.Message ?? throw new InvalidOperationException("Chat update requires a message."), cancellationToken).ConfigureAwait(false);
                metadata["chat.message_id"] = message.Id.ToString();
                return HumanInteractionExecutionResult.Completed(CompletedResponse(request, channel, thread, message, metadata), metadata);
            }

            case HumanInteractionOperation.Delete:
            {
                var messageId = ParseMessageId(request.MessageId, "Chat delete requires a message id.");
                await _store.DeleteMessageAsync(thread.Id, messageId, cancellationToken).ConfigureAwait(false);
                metadata["chat.message_id"] = messageId.ToString();
                return HumanInteractionExecutionResult.Completed(
                    new InteractionResponse
                    {
                        RequestId = request.Id,
                        Outcome = InteractionOutcome.Submitted,
                        Success = true,
                        MessageId = messageId.ToString(),
                        ThreadId = thread.Id.ToString(),
                        ConversationId = thread.Id.ToString(),
                        Channel = channel.ChannelType,
                        Metadata = metadata,
                    },
                    metadata
                );
            }

            case HumanInteractionOperation.Search:
            {
                var results = await _store.SearchMessagesAsync(request.RunId, request.Query ?? throw new InvalidOperationException("Chat search requires a query."), thread.Id, cancellationToken).ConfigureAwait(false);
                var messages = results
                    .Select(r => new InteractionMessage
                    {
                        MessageId = r.Id.ToString(),
                        ThreadId = r.ThreadId.ToString(),
                        ConversationId = r.ThreadId.ToString(),
                        AuthorId = r.AuthorId,
                        AuthorName = r.AuthorName,
                        Text = r.Text,
                        CreatedAt = r.CreatedAt,
                    })
                    .ToList();
                return HumanInteractionExecutionResult.Completed(
                    new InteractionResponse
                    {
                        RequestId = request.Id,
                        Outcome = InteractionOutcome.Submitted,
                        Success = true,
                        Messages = messages,
                        ResponseText = messages.Count == 0 ? string.Empty : string.Join(Environment.NewLine + Environment.NewLine, messages.Select(m => m.Text)),
                        ThreadId = thread.Id.ToString(),
                        ConversationId = thread.Id.ToString(),
                        Channel = channel.ChannelType,
                        Metadata = metadata,
                    },
                    metadata
                );
            }

            default:
                throw new InvalidOperationException($"Unsupported chat operation '{request.Operation}'.");
        }
    }

    public ValueTask<InteractionResponse?> TryResolveInboundAsync(InteractionRequest request, InteractionChannelInboundMessage inboundMessage, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(inboundMessage.ChannelType, "chat", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<InteractionResponse?>(null);

        if (!TryGetThreadId(request, out var threadId))
            return ValueTask.FromResult<InteractionResponse?>(null);

        var inboundThreadId = inboundMessage.ExternalConversationId ?? inboundMessage.ExternalChannelId;
        if (!Guid.TryParse(inboundThreadId, out var parsedThreadId) || parsedThreadId != threadId)
            return ValueTask.FromResult<InteractionResponse?>(null);

        return ValueTask.FromResult<InteractionResponse?>(new InteractionResponse
        {
            RequestId = request.Id,
            Outcome = ParseInboundOutcome(inboundMessage.Data),
            Success = true,
            ResponseText = inboundMessage.Text,
            MessageId = inboundMessage.ExternalMessageId,
            ThreadId = threadId.ToString(),
            ConversationId = threadId.ToString(),
            Channel = inboundMessage.ChannelType,
            RespondedBy = inboundMessage.ExternalUserName ?? inboundMessage.ExternalUserId,
            Data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?>(request.Metadata, StringComparer.OrdinalIgnoreCase),
            Messages =
            [
                new InteractionMessage
                {
                    MessageId = inboundMessage.ExternalMessageId,
                    ThreadId = threadId.ToString(),
                    ConversationId = threadId.ToString(),
                    AuthorId = inboundMessage.ExternalUserId,
                    AuthorName = inboundMessage.ExternalUserName,
                    Text = inboundMessage.Text,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });
    }

    public IReadOnlyList<string> GetActiveBundleIds(IResourceBindingReader binding)
    {
        // agentivity_artifacts is always active for the chat channel — it provides the
        // built-in widget vocabulary (charts, tables, code blocks…) that the LLM can use
        // to respond with structured content instead of plain markdown.
        var configured = binding.GetStringList("widget_bundles").Where(id => !string.IsNullOrWhiteSpace(id));
        return new[] { "agentivity_artifacts" }.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reads the channel's <c>custom_widgets</c>: a JSON array of <c>{ name, description, parametersSchema }</c>,
    /// where <c>parametersSchema</c> is a JSON Schema <b>object</b> (never a string holding JSON). Each widget
    /// is validated on its own — a malformed one is skipped and reported, it never takes the valid ones down
    /// with it — and every rejection is logged with the widget name and the reason: a declared widget that
    /// silently never reaches the agent is exactly what this used to do.
    /// </summary>
    public IReadOnlyList<AgUiCustomWidgetDefinition> GetCustomWidgets(IResourceBindingReader binding)
    {
        var raw = binding.GetString("custom_widgets");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Chat channel 'custom_widgets' is not valid JSON — no custom widget is offered to the agent.");
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger?.LogError("Chat channel 'custom_widgets' must be a JSON array of widget definitions (found {Kind}) — no custom widget is offered to the agent.", document.RootElement.ValueKind);
                return [];
            }

            var widgets = new List<AgUiCustomWidgetDefinition>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (TryReadCustomWidget(entry, out var widget, out var name, out var problem))
                    widgets.Add(widget!);
                else
                    _logger?.LogError("Chat channel custom widget '{Widget}' is ignored: {Problem}", name, problem);
            }

            return widgets;
        }
    }

    private static bool TryReadCustomWidget(JsonElement entry, out AgUiCustomWidgetDefinition? widget, out string name, out string problem)
    {
        widget = null;
        name = "(unnamed)";
        problem = string.Empty;

        if (entry.ValueKind != JsonValueKind.Object)
        {
            problem = "each widget must be a JSON object with name, description and parametersSchema.";
            return false;
        }

        if (entry.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nameElement.GetString()))
            name = nameElement.GetString()!;
        else
        {
            problem = "'name' must be a non-empty string.";
            return false;
        }

        if (!entry.TryGetProperty("parametersSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
        {
            problem = "'parametersSchema' is required and must be a JSON Schema object (not a string holding JSON).";
            return false;
        }

        var description = entry.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String ? descriptionElement.GetString() : null;
        widget = new AgUiCustomWidgetDefinition(name, description ?? string.Empty, schema.GetRawText());
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the <c>content</c> array from an <c>interact_with_human</c> tool call's raw
    /// arguments (stored in <c>Metadata["human_review.tool_arguments_json"]</c>). Returns
    /// <see langword="null"/> when this request wasn't sourced from that tool (e.g. the
    /// MissedAskHumanFallback text-only path, or a plain workflow-authored send) — callers fall
    /// back to the plain-text path in that case.
    /// </summary>
    private static List<JsonElement>? TryGetContentBlocks(InteractionRequest request)
    {
        if (request.Metadata?.TryGetValue("human_review.tool_arguments_json", out var raw) != true || raw?.ToString() is not { Length: > 0 } argumentsJson)
            return null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("content", out var contentElement) || JsonContentBlocks.NormalizeContentArrayElement(contentElement) is not { } array)
                return null;

            // Clone each element — the source JsonDocument (or the inner one parsed by
            // NormalizeContentArrayElement) is disposed/out of scope when this method returns.
            return array.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores <paramref name="blocks"/> as a single chat message whose <c>Metadata["blocks"]</c>
    /// carries the ordered list (text and/or widget blocks) — one message, one bubble, rendered
    /// front-to-back by the frontend (see <c>ChatMessage.blocks</c> in the SDKs). The
    /// message's plain <c>Text</c> is a text-only fallback (search/history/older clients); the
    /// widget props are the block's own arguments verbatim, no translation.
    /// </summary>
    private async ValueTask<Guid> PublishContentBlocksAsync(ChannelBinding channel, InteractionRequest request, InteractionThread thread, List<JsonElement> blocks, CancellationToken cancellationToken)
    {
        var normalizedBlocks = new List<Dictionary<string, object?>>();
        var fallbackTextParts = new List<string>();

        foreach (var block in blocks)
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                continue;

            var type = typeElement.GetString()!;
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                var text = block.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String ? textElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                normalizedBlocks.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["type"] = "text", ["text"] = text });
                fallbackTextParts.Add(text);
            }
            else
            {
                var widgetProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in block.EnumerateObject())
                {
                    if (string.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    widgetProps[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), JsonOpts);
                }

                normalizedBlocks.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["type"] = type, ["widgetProps"] = widgetProps });
            }
        }

        var messageMetadata = CreateMessageMetadata(request);
        messageMetadata["blocks"] = normalizedBlocks;
        var (authorId, authorName) = ResolveAuthor(channel, request);

        var stored = await _store
            .AddMessageAsync(
                thread.Id,
                new ThreadMessage
                {
                    Id = Guid.NewGuid(),
                    ThreadId = thread.Id,
                    RunId = thread.RunId,
                    AuthorType = ThreadAuthorType.Workflow,
                    AuthorId = authorId,
                    AuthorName = authorName,
                    Text = string.Join("\n\n", fallbackTextParts),
                    Metadata = messageMetadata,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return stored.Id;
    }

    private async ValueTask<InteractionThread> ResolveThreadAsync(ChannelBinding channel, InteractionRequest request, CancellationToken ct)
    {
        if (TryGetThreadId(request, out var threadId))
        {
            var existing = await _store.GetThreadAsync(threadId, ct).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }

        // GetOrCreateDefaultThreadAsync is the single authority for thread creation.
        // If AgentExecutable already created and persisted the default thread, this returns
        // the existing one. Otherwise it creates a new one. Either way: one thread per run.
        var newThread = await _store.GetOrCreateDefaultThreadAsync(request.RunId, request.ExecutionId, streamId: request.StreamId, ct).ConfigureAwait(false);

        // Only add the execution input message when the thread is freshly created (empty).
        // If AgentExecutable already created the thread and published messages, skip this
        // to avoid duplicating the user's initial input.
        if (_executionStore is not null && !string.IsNullOrWhiteSpace(request.ExecutionId))
        {
            var existingMessages = await _store.ListMessagesAsync(newThread.Id, ct).ConfigureAwait(false);
            if (existingMessages.Count == 0)
            {
                var execution = await _executionStore.GetAsync(request.ExecutionId, ct).ConfigureAwait(false);
                if (execution is not null && !string.IsNullOrWhiteSpace(execution.Input))
                {
                    await _store
                        .AddMessageAsync(
                            newThread.Id,
                            new ThreadMessage
                            {
                                Id = Guid.NewGuid(),
                                ThreadId = newThread.Id,
                                RunId = newThread.RunId,
                                AuthorType = ThreadAuthorType.User,
                                Text = execution.Input,
                            },
                            ct
                        )
                        .ConfigureAwait(false);
                }
            }
        }

        return newThread;
    }

    private void PublishHilGate(InteractionRequest request, Guid threadId, Guid messageId, string question)
    {
        if (_eventBus is null) { _logger?.LogWarning("ChatChannel has no event bus — HIL gate for request {RequestId} was not published.", request.Id); return; }
        _eventBus.Publish(
            new RunEvent(
                RunId: request.RunId.ToString(),
                DefinitionId: "chat",
                RunKind: RunKind.Workflow,
                Kind: RunEventKind.hil_gate_reached,
                EventName: "chat_hil_gate_reached",
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Payload: new
                {
                    requestId = request.Id,
                    threadId = threadId.ToString(),
                    messageId = messageId.ToString(),
                    question,
                    title = request.Title,
                    channelType = "chat",
                },
                Terminal: false,
                ExecutionId: request.ExecutionId,
                StreamId: request.StreamId
            )
        );
    }

    internal static bool TryGetThreadId(InteractionRequest request, out Guid threadId)
    {
        foreach (var candidate in new[] {
            request.ThreadId,
            request.ConversationId,
            GetMeta(request.Metadata, "chat.thread_id"),
            GetMeta(request.Metadata, "existing_thread_id"),
        })
            if (Guid.TryParse(candidate, out threadId))
                return true;
        threadId = Guid.Empty;
        return false;
    }

    private static string BuildOutboundText(InteractionRequest r) =>
        r.Operation == HumanInteractionOperation.Receive
            ? string.IsNullOrWhiteSpace(r.Message) ? "Awaiting a reply." : r.Message
            : r.Message ?? string.Empty;

    private static string BuildInteractionText(InteractionRequest r) =>
        !string.IsNullOrWhiteSpace(r.Message) ? r.Message.Trim()
        : !string.IsNullOrWhiteSpace(r.Description) ? r.Description.Trim()
        : r.Title?.Trim() ?? string.Empty;

    private static Dictionary<string, object?> CreateBaseMetadata(ChannelBinding channel, InteractionRequest request, InteractionThread thread) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["chat.thread_id"] = thread.Id.ToString(),
            ["chat.thread_title"] = thread.Title,
            ["chat.run_id"] = request.RunId.ToString(),
            ["chat.respondent"] = channel.Respondent,
        };

    /// <summary>
    /// Resolves the author identity for an assistant-produced message: <c>member.entityId</c>/
    /// <c>member.displayName</c> (set by <c>HumanLoopHelper.BuildInteractionRequest</c> for a team
    /// member's turn) take precedence, falling back to the channel's static
    /// <c>workflow_author_name</c> config (standalone agents, or a team member with no display
    /// name resolved) so existing single-agent behavior is unchanged.
    /// </summary>
    private static (string? AuthorId, string AuthorName) ResolveAuthor(ChannelBinding channel, InteractionRequest request)
    {
        var authorId = request.Metadata?.GetValueOrDefault("member.entityId") as string;
        var displayName = request.Metadata?.GetValueOrDefault("member.displayName") as string;
        var authorName = !string.IsNullOrWhiteSpace(displayName) ? displayName : channel.Properties.GetString("workflow_author_name") ?? "Workflow";
        return (authorId, authorName);
    }

    private static Dictionary<string, object?> CreateMessageMetadata(InteractionRequest request) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["interaction.request_id"] = request.Id.ToString(),
            ["interaction.kind"] = request.Kind.ToString(),
            ["interaction.operation"] = request.Operation.ToString(),
            ["interaction.awaiting_response"] = request.Operation is HumanInteractionOperation.SendAndWait or HumanInteractionOperation.Receive,
        };

    private static InteractionResponse CompletedResponse(InteractionRequest request, ChannelBinding channel, InteractionThread thread, ThreadMessage message, IReadOnlyDictionary<string, object?> metadata) =>
        new()
        {
            RequestId = request.Id,
            Outcome = InteractionOutcome.Submitted,
            Success = true,
            ResponseText = message.Text,
            MessageId = message.Id.ToString(),
            ThreadId = thread.Id.ToString(),
            ConversationId = thread.Id.ToString(),
            Channel = channel.ChannelType,
            Metadata = metadata,
        };

    private static Guid ParseMessageId(string? raw, string error) => Guid.TryParse(raw, out var id) ? id : throw new InvalidOperationException(error);

    private static string? GetMeta(IReadOnlyDictionary<string, object?> meta, string key) => meta.TryGetValue(key, out var v) ? v?.ToString() : null;

    internal static InteractionOutcome ParseInboundOutcome(IReadOnlyDictionary<string, object?> data)
    {
        if (!data.TryGetValue("interaction.outcome", out var raw) || raw is null)
            return InteractionOutcome.Submitted;
        return raw.ToString()?.Trim().ToLowerInvariant() switch
        {
            "approved" => InteractionOutcome.Approved,
            "rejected" => InteractionOutcome.Rejected,
            "revised" => InteractionOutcome.Revised,
            "timedout" or "timed_out" or "timed-out" => InteractionOutcome.TimedOut,
            "cancelled" or "canceled" => InteractionOutcome.Cancelled,
            _ => InteractionOutcome.Submitted,
        };
    }

}
