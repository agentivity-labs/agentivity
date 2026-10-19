using System.Text;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Module.Assistant.Changesets;

namespace Agentivity.Module.Assistant.Loop;

public sealed class AssistantLoopOptions
{
    /// <summary>
    /// The most model calls one turn may make before the assistant pauses. It is a pause, not a failure: the person sees every
    /// attempt as it happens and says "continue" (or stops it earlier), so the limit only has to catch a run that got away.
    /// </summary>
    public int MaxSteps { get; set; } = 60;

    /// <summary>
    /// The most tokens one turn may process, all calls together, before it pauses. Most of them are the instructions and the
    /// catalog read back from the provider's cache on every call, so this is far above what a turn is billed.
    /// </summary>
    public int MaxTokensPerTurn { get; set; } = 3_000_000;

    /// <summary>How many earlier messages of the conversation the model is shown.</summary>
    public int MaxHistoryMessages { get; set; } = 30;

    public double Temperature { get; set; } = 0.2;

    /// <summary>
    /// The most the model may write in one answer, or null (the default) for no limit of ours: the model then writes as much
    /// as it can (a provider that requires a figure, like Anthropic, is given the client's own maximum). A whole team
    /// described in one go is long, and a limit set here only cuts it in the middle; set one only for a model that needs it.
    /// </summary>
    public int? MaxResponseTokens { get; set; }

    /// <summary>How many times in a turn an answer cut off by the model's own limit is asked again, shorter, before the turn stops.</summary>
    public int MaxCutOffRetries { get; set; } = 2;

    /// <summary>How many times the same call may fail the same way before the assistant stops and says so.</summary>
    public int StuckRepeats { get; set; } = 3;

    /// <summary>
    /// Up to this many step types the catalog in the instructions gives each type's inputs and outputs; beyond it, only an
    /// identifier and a line, and the model reads the rest on demand.
    /// </summary>
    public int CatalogMaxDetailed { get; set; } = 250;
}

/// <summary>One thing a person said, with where they were looking.</summary>
public sealed record AssistantTurnRequest(string Message, string? ChangesetId = null, AssistantContext? Context = null, string? AgentModelId = null);

public sealed record AssistantTurnResult(string ChangesetId, string Reason, string Text, int Steps, int TokensUsed);

/// <summary>
/// One turn of the assistant: the model is given the instructions, the work so far and the tools; it calls tools until it has
/// something to tell the person or something to ask. Everything it does goes through <see cref="AssistantTools"/>, which cannot
/// save, publish or delete from the workspace.
/// </summary>
public sealed class AssistantLoop(IAssistantModelProvider models, AssistantTools tools, IBuilderApi builder, IChangesetStore store, ICapabilityRegistry registry, AssistantLoopOptions? options = null, ILogger<AssistantLoop>? logger = null)
{
    private readonly AssistantLoopOptions _options = options ?? new AssistantLoopOptions();

    public async Task<AssistantTurnResult> RunAsync(AssistantTurnRequest request, IAssistantEventSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("A message is required.", nameof(request));

        var runId = Guid.NewGuid().ToString("N");

        var changeset = request.ChangesetId is null
            ? await builder.CreateChangesetAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await store.GetAsync(request.ChangesetId, cancellationToken).ConfigureAwait(false);

        if (changeset is null || changeset.Status != "open")
        {
            await sink.EmitAsync(new RunFailed("changeset_not_found", "This piece of work is not open any more."), cancellationToken).ConfigureAwait(false);
            return new AssistantTurnResult(request.ChangesetId ?? "", "error", "", 0, 0);
        }

        var id = changeset.Id;

        // The person is answering the plan the assistant stated at the end of the last turn: it is confirmed. (If they asked for
        // changes, the assistant states a new plan, which is unconfirmed until they answer that one.)
        // Except when they picked one of the answers that ask for a change: that is not a yes. What their answer means is
        // also said to the model in so many words, beside the answer: it only ever sees the conversation, not this state.
        string? answerNote = null;
        if (changeset.Plan is { Confirmed: false } statedPlan)
        {
            bool Picked(string option) => string.Equals(option.Trim(), request.Message.Trim(), StringComparison.OrdinalIgnoreCase);

            if (statedPlan.Options.Skip(1).Any(Picked))
            {
                answerNote = "(The person asks for a change to the plan you stated. Find out what they want changed, then state the revised plan with set_plan. Build nothing yet.)";
            }
            else
            {
                statedPlan.Confirmed = true;
                statedPlan.GoAhead = statedPlan.Options.Take(1).Any(Picked);
                await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);
                answerNote = statedPlan.GoAhead
                    ? "(The person has just agreed to the plan you stated. Do not state it again and do not ask them to confirm again: build it now, and keep going until it is done.)"
                    : "(This is the person's answer to the plan you stated. If it agrees, build now without stating the plan again. If it asks for a change, state the revised plan with set_plan.)";
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AgentModelId) && !string.Equals(changeset.AgentModel?.ConnectionId, request.AgentModelId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                changeset = await builder.SetAgentModelAsync(id, request.AgentModelId, cancellationToken).ConfigureAwait(false) ?? changeset;
            }
            catch (ArgumentException ex)
            {
                await sink.EmitAsync(new RunFailed("agent_model_unknown", ex.Message), cancellationToken).ConfigureAwait(false);
                return new AssistantTurnResult(id, "error", "", 0, 0);
            }
        }

        await sink.EmitAsync(new RunStarted(runId, id), cancellationToken).ConfigureAwait(false);

        // What the person said is kept first: a failure below must not lose it.
        changeset.Conversation.Add(new ConversationMessage { Role = "user", Content = request.Message.Trim() });
        await store.SaveAsync(changeset, cancellationToken).ConfigureAwait(false);

        AssistantModel model;
        try
        {
            model = await models.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AssistantModelUnavailableException ex)
        {
            await sink.EmitAsync(new RunFailed(ex.Code, ex.Message), cancellationToken).ConfigureAwait(false);
            return new AssistantTurnResult(id, "error", ex.Message, 0, 0);
        }

        var messages = new List<LlmChatMessage> { new("system", AssistantPrompt.Build(registry.Current, changeset, request.Context, _options.CatalogMaxDetailed)) };
        // The assistant is given back the whole of its earlier turns (what it said, planned, did and asked), not only the last
        // line the person read: with a bare question for memory it would not know a plan was stated, and state it again.
        foreach (var earlier in changeset.Conversation.SkipLast(1).TakeLast(_options.MaxHistoryMessages))
            messages.Add(new LlmChatMessage(earlier.Role, string.IsNullOrWhiteSpace(earlier.Memory) ? earlier.Content : earlier.Memory));
        messages.Add(new LlmChatMessage("user", answerNote is null ? request.Message.Trim() : request.Message.Trim() + "\n\n" + answerNote));

        var conversationOptions = new LlmConversationOptions(Temperature: _options.Temperature, MaxTokens: _options.MaxResponseTokens, ToolChoiceMode: LlmToolChoiceMode.Auto, AllowParallelToolCalls: false);
        var work = new List<string>();
        var failures = new Dictionary<string, int>();
        var cutOffs = 0;
        var tokens = 0;
        var cached = 0;
        var steps = 0;
        var changedThisTurn = false;
        var gateRounds = 0;
        // The agents as they were when the turn began: the ones built or changed during it are checked for their tools once.
        var agentsBefore = AgentGraphs(changeset);
        var toolsReviewed = false;
        string finalText = "";
        string reason = "answered";
        Question? asked = null;
        // What the model itself wrote during the turn, and the plan it stated: kept for its memory of the turn.
        var said = new List<string>();
        AssistantPlan? stated = null;

        while (true)
        {
            if (steps >= _options.MaxSteps)
            {
                reason = "step_limit";
                finalText = "I have done a lot in one go and I am stopping here so you can look at it. Tell me to continue and I will pick up where I left off.";
                await sink.EmitAsync(new AssistantText(finalText), cancellationToken).ConfigureAwait(false);
                break;
            }

            if (tokens >= _options.MaxTokensPerTurn)
            {
                reason = "token_limit";
                finalText = "I have reached the amount of work I allow myself in one turn. Tell me to continue and I will pick up where I left off.";
                await sink.EmitAsync(new AssistantText(finalText), cancellationToken).ConfigureAwait(false);
                break;
            }

            // The instructions are the same for every step of a turn (the work as it stood when the turn began): that is what lets
            // the provider cache them. What the model does during the turn comes back to it in the results of its own calls.
            steps++;
            Agentivity.Domain.Common.OperationResult<LlmConversationResponse> completion;
            try
            {
                completion = await model.Client.CompleteConversationAsync(messages, tools.Definitions, conversationOptions, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Agentivity.Domain.LlmCallException cut) when (cut.Code == "llm_truncated")
            {
                // The answer was longer than the model is allowed to write at once: nothing of it arrived. It is not the
                // person's problem to solve: the model is told, and asked for the same work in smaller pieces.
                if (cutOffs >= _options.MaxCutOffRetries)
                {
                    reason = "cut_off";
                    finalText = "What I was writing is too long for me to send in one go, even in smaller pieces. Could you ask me for it part by part (for example a few agents at a time)?";
                    await sink.EmitAsync(new AssistantText(finalText), cancellationToken).ConfigureAwait(false);
                    break;
                }

                cutOffs++;
                await sink.EmitAsync(new ToolStarted("cut-" + cutOffs, "validate", "Writing it in smaller pieces"), cancellationToken).ConfigureAwait(false);
                await sink.EmitAsync(new ToolFinished("cut-" + cutOffs, "validate", false, "My answer was too long to send at once"), cancellationToken).ConfigureAwait(false);
                messages.Add(new LlmChatMessage("user", "Your last answer was cut off because it was too long, so none of it was received and nothing was built. Do the same work in smaller pieces: several build_from_spec calls of a few entities each, with \"partial\": true while more is to come (the solution may then be described in a later call), and prompts kept to what matters. Start again from what the work in progress holds now."));
                continue;
            }

            if (completion.IsFailure || completion.Value is null)
            {
                logger?.LogWarning("Assistant model call failed: {Error}", completion.Error);
                await sink.EmitAsync(new RunFailed("model_error", "The model did not answer. " + (completion.Error ?? "")), cancellationToken).ConfigureAwait(false);
                reason = "error";
                finalText = "";
                await SaveAssistantMessageAsync(id, "", work, [], null, Remember(said, stated, work, null, ""), cancellationToken).ConfigureAwait(false);
                return new AssistantTurnResult(id, reason, finalText, steps, tokens);
            }

            var response = completion.Value;
            tokens += response.TokensUsed;
            cached += response.CachedInputTokens;
            var text = response.Content?.Trim() ?? "";

            if (response.ToolCalls.Count == 0)
            {
                // The model thinks it is done. If it changed the work and left errors it never looked at, send it back once or
                // twice with the list: a small model often forgets the checking step the instructions ask for.
                // What was built is compared with what the plan said would be built: saying "your team is ready" with the agents
                // made and no team is the kind of thing a model does, and the person would believe it.
                if (changedThisTurn && gateRounds < MaxGateRounds && await MissingFromPlanAsync(id, cancellationToken).ConfigureAwait(false) is { } missing)
                {
                    gateRounds++;
                    messages.Add(new LlmChatMessage("assistant", response.Content ?? ""));
                    messages.Add(new LlmChatMessage("user", "Before you answer: the work is not complete. " + missing + " Build what is missing now, referring to what already exists by its name; do not say it is ready until it exists. If you decided not to build it, say so plainly and why."));
                    await sink.EmitAsync(new ToolStarted("plan-" + gateRounds, "validate", "Comparing with the plan"), cancellationToken).ConfigureAwait(false);
                    await sink.EmitAsync(new ToolFinished("plan-" + gateRounds, "validate", false, "Part of the plan is not built yet"), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (changedThisTurn && !toolsReviewed && await ToolsToReviewAsync(id, agentsBefore, cancellationToken).ConfigureAwait(false) is { } agents)
                {
                    toolsReviewed = true;
                    messages.Add(new LlmChatMessage("assistant", response.Content ?? ""));
                    messages.Add(new LlmChatMessage("user", "Before you answer: check that each agent you built or changed has the tools its job needs. " + agents + " For each one, name what its instructions and its knowledge rely on: today's date (a deadline, a warranty, \"within 30 days\"), reading or changing a table, the web, sending an e-mail, asking a person… Each of these needs a tool connected to it: find it with search_capabilities (its \"when to use\" says which agents need it) and connect it; remove a tool its instructions never use. What you cannot connect is said plainly to the person, as something they provide. If every agent already has what it needs, answer now."));
                    await sink.EmitAsync(new ToolStarted("tools-review", "validate", "Checking each agent has the tools its job needs"), cancellationToken).ConfigureAwait(false);
                    await sink.EmitAsync(new ToolFinished("tools-review", "validate", true, "Checked the tools of each agent"), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (changedThisTurn && gateRounds < MaxGateRounds && await UnresolvedErrorsAsync(id, cancellationToken).ConfigureAwait(false) is { } problems)
                {
                    gateRounds++;
                    messages.Add(new LlmChatMessage("assistant", response.Content ?? ""));
                    messages.Add(new LlmChatMessage("user", "Before you answer: I checked the work and it still has problems. Fix the ones you can with apply_operations (for a missing connection, search for what is needed and connect it); if only the person can provide something, say so plainly. Problems: " + problems));
                    await sink.EmitAsync(new ToolStarted("check-" + gateRounds, "validate", "Checking the work"), cancellationToken).ConfigureAwait(false);
                    await sink.EmitAsync(new ToolFinished("check-" + gateRounds, "validate", true, "Checked: something is left to fix"), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // The assistant cannot save. An answer that says it did is corrected before the person reads it.
                if (gateRounds < MaxGateRounds && SavedClaims.Claims(text))
                {
                    gateRounds++;
                    messages.Add(new LlmChatMessage("assistant", response.Content ?? ""));
                    messages.Add(new LlmChatMessage("user", "Correction: your answer says something is saved or published. Nothing is: you cannot save, and the person reviews and saves it themselves. Say so plainly and answer again."));
                    continue;
                }

                finalText = text;
                if (text.Length > 0)
                {
                    said.Add(text);
                    await sink.EmitAsync(new AssistantText(text), cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            messages.Add(new LlmChatMessage("assistant", response.Content, ToolCalls: response.ToolCalls));
            if (text.Length > 0)
            {
                said.Add(text);
                await sink.EmitAsync(new AssistantText(text), cancellationToken).ConfigureAwait(false);
            }

            var stop = false;
            foreach (var call in response.ToolCalls)
            {
                JsonObject args;
                try
                {
                    args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
                }
                catch (System.Text.Json.JsonException)
                {
                    args = [];
                    messages.Add(new LlmChatMessage("tool", "{\"ok\":false,\"error\":\"The arguments were not valid JSON. Send them again as a JSON object.\"}", ToolCallId: call.Id));
                    await sink.EmitAsync(new ToolFinished(call.Id, call.Name, false, "I sent something malformed."), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await sink.EmitAsync(new ToolStarted(call.Id, call.Name, Describing(call.Name, args)), cancellationToken).ConfigureAwait(false);
                var outcome = await tools.ExecuteAsync(id, call.Name, args, cancellationToken).ConfigureAwait(false);
                await sink.EmitAsync(new ToolFinished(call.Id, call.Name, outcome.Ok, outcome.Summary), cancellationToken).ConfigureAwait(false);
                messages.Add(new LlmChatMessage("tool", outcome.Content, ToolCallId: call.Id));

                if (outcome.Ok && outcome.Summary.Length > 0 && call.Name is not ("ask_user" or "ask_connection"))
                    work.Add(outcome.Summary);

                changedThisTurn |= outcome.ChangedChangeset;
                if (outcome.ChangedChangeset && await store.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } current)
                    await sink.EmitAsync(new ChangesetChanged(BuilderApi.Summarize(current)), cancellationToken).ConfigureAwait(false);
                if (outcome.Plan is not null)
                {
                    stated = outcome.Plan;
                    await sink.EmitAsync(new PlanChanged(outcome.Plan), cancellationToken).ConfigureAwait(false);
                }

                if (outcome.Question is not null)
                {
                    asked = outcome.Question;
                    finalText = outcome.Question.Text;
                    reason = "question";
                    await sink.EmitAsync(outcome.Question, cancellationToken).ConfigureAwait(false);
                    stop = true;
                    break;
                }

                if (!outcome.Ok)
                {
                    var signature = call.Name + "|" + call.ArgumentsJson;
                    failures[signature] = failures.GetValueOrDefault(signature) + 1;
                    if (failures[signature] >= _options.StuckRepeats)
                    {
                        reason = "stuck";
                        finalText = $"I keep running into the same problem and I would rather stop than go round in circles: {outcome.Summary} Could you tell me how you would like me to handle it?";
                        await sink.EmitAsync(new AssistantText(finalText), cancellationToken).ConfigureAwait(false);
                        stop = true;
                        break;
                    }
                }
            }

            if (stop)
                break;
        }

        await SaveAssistantMessageAsync(id, finalText, work, asked?.Options ?? [], asked?.Pick, Remember(said, stated, work, asked, finalText), cancellationToken).ConfigureAwait(false);
        await sink.EmitAsync(new RunFinished(steps, tokens, reason, cached), cancellationToken).ConfigureAwait(false);
        return new AssistantTurnResult(id, reason, finalText, steps, tokens);
    }

    private const int MaxGateRounds = 2;

    /// <summary>
    /// What the plan says will exist and the work does not have, by kind, in words for the model; null when nothing is missing
    /// or when the plan does not say what it builds.
    /// </summary>
    private async Task<string?> MissingFromPlanAsync(string changesetId, CancellationToken ct)
    {
        var changeset = await store.GetAsync(changesetId, ct).ConfigureAwait(false);
        if (changeset is null)
            return null;

        var live = changeset.Entities.Where(e => e.State != EntityStates.Deleted).ToList();
        if (changeset.Plan is not { Builds.Count: > 0 } plan)
            return LoosePieces(live);

        var gaps = new List<string>();
        foreach (var group in plan.Builds.GroupBy(b => b.Kind))
        {
            var built = live.Count(e => e.Kind == group.Key);
            if (built >= group.Count())
                continue;

            // Named ones first: the ones the plan names and nothing built carries.
            var names = group.Select(b => b.Name).Where(n => n.Length > 0 && !live.Any(e => e.Kind == group.Key && string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            gaps.Add($"{group.Count() - built} {group.Key}{(group.Count() - built == 1 ? "" : "s")} missing{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")}");
        }

        if (gaps.Count == 0)
            return LoosePieces(live);

        var have = live.Count == 0 ? "nothing" : string.Join(", ", live.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")} ({string.Join(", ", g.Select(e => e.Name))})"));
        return $"It has: {have}. The plan also calls for: {string.Join("; ", gaps)}.";
    }

    /// <summary>
    /// Whatever the plan says: a solution is one thing that holds the rest (a team over its agents, a workflow over what it
    /// calls). Several pieces that nothing refers to are parts with nothing assembling them, and are said so to the model.
    /// </summary>
    private static string? LoosePieces(List<ChangesetEntity> live)
    {
        if (live.Count < 2)
            return null;

        var drafts = live.ToDictionary(e => e.Id, e => e.Draft?.ToJsonString() ?? "");
        var top = live.Where(e => !live.Any(o => o.Id != e.Id && drafts[o.Id].Contains(e.Id, StringComparison.Ordinal))).ToList();
        if (top.Count < 2)
            return null;

        return $"It has {top.Count} separate pieces that nothing holds together: {string.Join(", ", top.Select(e => $"{e.Name} ({e.Kind})"))}. A solution is one main entity that contains the others: a team whose members are these agents, or a workflow that calls them.";
    }

    /// <summary>Each agent of the work, by id: its graph as written, to tell which ones a turn built or changed.</summary>
    private static Dictionary<string, string> AgentGraphs(Changeset changeset) =>
        changeset.Entities.Where(e => e.Kind == EntityKinds.Agent && e.State != EntityStates.Deleted).ToDictionary(e => e.Id, e => e.Draft["graph"]?.ToJsonString() ?? "");

    /// <summary>
    /// The agents built or changed since <paramref name="before"/>, each with the tools and the knowledge connected to it, in
    /// one line for the model; null when none was.
    /// </summary>
    private async Task<string?> ToolsToReviewAsync(string changesetId, Dictionary<string, string> before, CancellationToken ct)
    {
        if (await store.GetAsync(changesetId, ct).ConfigureAwait(false) is not { } changeset)
            return null;

        var touched = AgentGraphs(changeset).Where(a => !before.TryGetValue(a.Key, out var was) || was != a.Value).Select(a => changeset.Entities.First(e => e.Id == a.Key)).ToList();
        if (touched.Count == 0)
            return null;

        string Connected(JsonObject graph, string port)
        {
            var nodes = (graph["nodes"] as JsonArray)?.OfType<JsonObject>().ToDictionary(n => n["id"]?.ToString() ?? "", n => n["nodeType"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase) ?? [];
            var names = (graph["connections"] as JsonArray)?.OfType<JsonObject>()
                .Where(c => string.Equals(c["toPort"]?.ToString(), port, StringComparison.OrdinalIgnoreCase))
                .Select(c => nodes.GetValueOrDefault(c["from"]?.ToString() ?? "") is { Length: > 0 } type ? registry.Current.Get(CapabilityKinds.Node, type)?.Name ?? type : c["from"]?.ToString() ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [];
            return names.Count == 0 ? "" : string.Join(", ", names);
        }

        return string.Join(" ", touched.Select(a =>
        {
            var graph = a.Draft["graph"] as JsonObject ?? [];
            var tools = Connected(graph, "tools");
            var knowledge = Connected(graph, "knowledge");
            return $"'{a.Name}': {(tools.Length == 0 ? "no tools" : "tools " + tools)}{(knowledge.Length == 0 ? "" : "; knowledge " + knowledge)}.";
        }));
    }

    /// <summary>The errors of the work in progress as one line for the model, or null when there are none.</summary>
    private async Task<string?> UnresolvedErrorsAsync(string changesetId, CancellationToken ct)
    {
        var issues = await builder.ValidateAsync(changesetId, ct).ConfigureAwait(false);
        var errors = issues?.Where(i => i.Severity == "error").ToList();
        if (errors is not { Count: > 0 })
            return null;
        return string.Join(" | ", errors.Take(6).Select(e => e.Message + (string.IsNullOrWhiteSpace(e.Hint) ? "" : " Hint: " + e.Hint)));
    }

    /// <summary>
    /// What the assistant is given back of a turn when the conversation goes on: what it said, the plan it stated, what it
    /// did and what it asked. The notes are its own, in the first person, so that it reads them as things it did.
    /// </summary>
    internal static string Remember(IReadOnlyList<string> said, AssistantPlan? stated, IReadOnlyList<string> work, Question? asked, string finalText)
    {
        var done = work.Where(w => !string.IsNullOrWhiteSpace(w)).Take(12).ToList();

        // A plain question, with nothing said or done before it, is remembered as the question it was.
        if (asked is not null && stated is null && done.Count == 0 && !said.Any(s => !string.IsNullOrWhiteSpace(s)))
            return asked.Options.Count == 0 ? asked.Text.Trim() : $"{asked.Text.Trim()}\n(I offered these answers: {string.Join(" | ", asked.Options)}.)";

        var sb = new StringBuilder();
        foreach (var text in said.Where(s => !string.IsNullOrWhiteSpace(s)))
            sb.AppendLine(text.Trim());
        if (said.Count == 0 && asked is null && finalText.Length > 0)
            sb.AppendLine(finalText);

        if (stated is not null)
        {
            sb.Append("(I stated this plan to the person and asked them to confirm it. Goal: ").Append(stated.Brief.Trim());
            if (stated.Steps.Count > 0)
                sb.Append(" Steps: ").Append(string.Join("; ", stated.Steps)).Append('.');
            if (stated.Builds.Count > 0)
                sb.Append(" To build: ").Append(string.Join(", ", stated.Builds.Select(b => $"{b.Kind} \"{b.Name}\""))).Append('.');
            if (stated.Needs.Count > 0)
                sb.Append(" Needed from the person: ").Append(string.Join("; ", stated.Needs)).Append('.');
            sb.AppendLine(")");
        }

        if (done.Count > 0)
            sb.Append("(What I did in this turn: ").Append(string.Join("; ", done)).AppendLine(".)");

        if (asked is not null)
        {
            sb.Append("(I then asked the person: \"").Append(asked.Text.Trim()).Append('"');
            if (asked.Options.Count > 0)
                sb.Append(" and offered these answers: ").Append(string.Join(" | ", asked.Options));
            sb.AppendLine(".)");
        }

        return sb.ToString().Trim();
    }

    private async Task SaveAssistantMessageAsync(string changesetId, string text, List<string> work, IReadOnlyList<string> options, ConnectionChoice? pick, string memory, CancellationToken ct)
    {
        var current = await store.GetAsync(changesetId, ct).ConfigureAwait(false);
        if (current is null)
            return;
        current.Conversation.Add(new ConversationMessage { Role = "assistant", Content = text, Work = [.. work.Take(12)], Options = [.. options], Pick = pick, Memory = memory.Length > 0 && memory != text ? memory : null });
        await store.SaveAsync(current, ct).ConfigureAwait(false);
    }

    /// <summary>What a tool call is about to do, for the person to read: names, not identifiers.</summary>
    private string Describing(string tool, JsonObject args)
    {
        static string? Text(JsonObject a, string n) => a[n] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        switch (tool)
        {
            case "search_capabilities":
                return $"Looking for “{Text(args, "query") ?? "what fits"}”";
            case "describe_capability":
                var item = Text(args, "kind") is { } k && Text(args, "id") is { } i ? registry.Current.Get(k, i) : null;
                return $"Reading about “{item?.Name ?? Text(args, "id") ?? "it"}”";
            case "list_capabilities":
                return "Browsing what is available";
            case "list_workspace":
                return "Checking what already exists";
            case "list_connections":
                return "Checking your connections";
            case "list_assets":
                return "Looking at your data";
            case "get_entity":
                return $"Reading “{Text(args, "entity") ?? "it"}”";
            case "build_from_spec":
                return "Building from the description";
            case "apply_operations":
                var count = (args["operations"] as JsonArray)?.Count ?? 0;
                return $"Building ({count} change{(count == 1 ? "" : "s")})";
            case "validate":
                return "Checking the work";
            case "set_plan":
                return "Drafting the plan";
            case "ask_user":
                return "Asking you a question";
            case "ask_connection":
                return "Asking you to choose a connection";
            default:
                return "Working";
        }
    }
}
