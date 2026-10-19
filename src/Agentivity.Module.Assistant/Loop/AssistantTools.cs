using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Module.Assistant.Changesets;
using Agentivity.Module.Assistant.Operations;

namespace Agentivity.Module.Assistant.Loop;

/// <summary>What a tool call gave back: the text the model reads, a plain-words account for the person, and what changed.</summary>
public sealed class ToolOutcome
{
    /// <summary>What the model reads.</summary>
    public string Content { get; init; } = "";

    /// <summary>What the person sees, in plain words.</summary>
    public string Summary { get; init; } = "";

    /// <summary>False when the tool refused or failed; the model is told why and may try again.</summary>
    public bool Ok { get; init; } = true;

    /// <summary>True when the changeset changed.</summary>
    public bool ChangedChangeset { get; init; }

    public AssistantPlan? Plan { get; init; }

    /// <summary>When set, the turn ends here and the person is asked.</summary>
    public Question? Question { get; init; }
}

/// <summary>
/// The tools the assistant may call. They are the whole of its reach: discovering what the platform offers, looking at what
/// exists, building in the changeset, checking it, stating the plan and asking the person. There is deliberately no tool that
/// saves, publishes or deletes from the workspace.
/// </summary>
public sealed class AssistantTools(ICapabilityRegistry registry, IEntityLookup lookup, IBuilderApi builder, IChangesetStore store, IConnectionLookup? connections = null, SpecBuilder? specs = null, IAssetLookup? assets = null)
{
    /// <summary>
    /// When true (the default) nothing new is created until a plan has been stated and the person has answered it. This is
    /// enforced here, not left to the model's judgement of what is simple.
    /// </summary>
    public bool RequirePlanFirst { get; init; } = true;

    /// <summary>Longest result handed to the model; beyond it the result is cut and the model told to narrow its request.</summary>
    public const int MaxResultChars = 6000;

    internal static readonly string[] OperationReference =
    [
        "create_entity: kind (workflow|agent|team), name, [role, root]",
        "rename_entity: entity, name",
        "set_role: entity, role  (an agent's role: one or two sentences on what it handles and what it does not; a team chooses its members by it)",
        "set_tags: entity, tags[]",
        "open_entity: entity (id or name of something already saved: brings it into the work with everything it holds, to change it)",
        "delete_entity: entity",
        "set_root: entity",
        "add_node: entity, type, [id, label, inputs{}, position{x,y}]  (a step of the flow: workflow or agent; never a resource)",
        "add_resource: entity, to (the step that uses it), type (a resource type), [port, id, label, inputs{}]  (a model, Knowledge Base, session, channel or tool: it is created and connected to that step for you)",
        "complete_resources: entity, [node]  (gives every step the resources it requires and lacks, when there is no doubt which; reports the choices otherwise)",
        "set_input: entity, node, input, value",
        "remove_input: entity, node, input",
        "connect: entity, from, to, [fromPort, toPort]  (the flow between steps: data from an output port to an input port; for a resource use add_resource)",
        "disconnect: entity, from, to, [fromPort, toPort]",
        "move_node: entity, node, x, y",
        "remove_node: entity, node",
        "set_entry: entity, node",
        "set_orchestrator: entity, orchestrator  (team)",
        "add_member: entity, member (id or name of an existing agent, team or workflow), group (its theme), [position, display_name, role]  (team)",
        "set_member_group: entity, position, group  (the theme a member belongs to; members that deal with the same thing share one)",
        "remove_member: entity, position",
        "set_manager: entity, position",
        "set_goal: entity, goal",
        "connect_members: entity, from, to, type  (positions; type must be one the orchestrator accepts)",
        "disconnect_members: entity, from, to",
    ];

    private static JsonObject Tool(string name, string description, string schema) => LlmToolDefinitionJson.Create(name, description, schema);

    /// <summary>The tools, as the model is told about them.</summary>
    public IReadOnlyList<JsonObject> Definitions { get; } =
    [
        Tool(
            "search_capabilities",
            "Find what the platform offers for a need, in plain words: steps (nodes), visual components (widgets), ways a team works (orchestrators), ways an agent reasons (agent modes). Returns short summaries; use describe_capability for the details.",
            """{"type":"object","properties":{"query":{"type":"string","description":"What you need, in plain words, e.g. 'send an e-mail to a customer'"},"kind":{"type":"string","enum":["node","widget","orchestrator","agent_mode"]},"limit":{"type":"integer","minimum":1,"maximum":15}},"required":["query"]}"""
        ),
        Tool(
            "describe_capability",
            "Read everything about one capability: what it does, when to use it, its inputs (required ones marked), outputs, ports and effects. Always do this before using a step you have not used yet.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["node","widget","orchestrator","agent_mode"]},"id":{"type":"string"}},"required":["kind","id"]}"""
        ),
        Tool(
            "list_capabilities",
            "Browse capabilities of one kind, optionally one family (category), as short summaries.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["node","widget","orchestrator","agent_mode"]},"category":{"type":"string"}},"required":["kind"]}"""
        ),
        Tool(
            "list_workspace",
            "List what already exists in the saved workspace (agents, teams, workflows), optionally of one kind and matching a word in the name or tags.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["workflow","agent","team"]},"query":{"type":"string"}}}"""
        ),
        Tool(
            "list_connections",
            "List the accounts and connections the person already has (model providers, mailboxes, sites…): id, name and kind. Use one of these ids for a step input that takes a connection or credential instead of asking the person; secrets are never shown. Optionally filter by kind; kind \"model\" gives every connection a language model can run on, of all providers.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"e.g. model, smtp"}}}"""
        ),
        Tool(
            "list_assets",
            "List the assets of the workspace a step input can point at: an input written \"(AssetRef: <kind>)\" in a capability card, such as the table a Data Table tool reads (kind \"datatable\"). Gives each one's id, name, folder and what it holds (a table's columns). Use the id as the input's value, for the one the person means; optionally filter by a word of its name or folder.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"The asset kind of the input, e.g. datatable"},"query":{"type":"string","description":"A word of its name or folder"}}}"""
        ),
        Tool(
            "get_entity",
            "Read one entity of the changeset or of the workspace: its steps and connections (workflow, agent) or its members and pattern (team). Use full=true only when you need every value.",
            """{"type":"object","properties":{"entity":{"type":"string","description":"id or name"},"full":{"type":"boolean"}},"required":["entity"]}"""
        ),
        Tool(
            "build_from_spec",
            "Build whole workflows, agents or teams from a light description written with the catalog's type identifiers: {\"workflow\"|\"agent\"|\"team\": name, \"role\": what it handles and what it does not (every agent has one), \"steps\": [{id, type, inputs}], \"resources\": [{id, type}], \"connections\": [[from, to] | {resource, for}]}. For several things: {\"solution\": {\"kind\", \"name\"}, \"entities\": [..]}, where \"solution\" names the one the person runs and which contains the others (a team with its agents: the team). The platform completes it (ports, ids, positions, missing resources) and checks it; you get every problem at once and nothing is built unless it is sound.",
            """{"type":"object","properties":{"spec":{"type":"object","description":"The description"}},"required":["spec"]}"""
        ),
        Tool(
            "apply_operations",
            "Change the work in progress. Write the operations in 'commands', one per line: the operation name, then key=value pairs. A value is a bare word, a \"double-quoted text\", or JSON for an object or list. Example line: add_node entity=\"Shop assistant\" type=ai.node inputs={\"prompt\":\"Answer politely\"}. (A JSON list in 'operations', each {\"op\": name, ...arguments}, is also accepted.) Operations are applied in order; at the first one refused it stops: the ones before it are kept, the ones after are not run, and you get the reason and the valid options. Send only the refused one, corrected, and the rest. Set atomic=true for all-or-nothing. Operations: " + string.Join("; ", OperationReference),
            """{"type":"object","properties":{"commands":{"type":"string","description":"One operation per line: name key=value key=value"},"operations":{"type":"array","items":{"type":"object","properties":{"op":{"type":"string"}},"required":["op"]}},"atomic":{"type":"boolean"},"label":{"type":"string","description":"A few words on what this batch does"}}}"""
        ),
        Tool(
            "validate",
            "Check the whole work in progress against the platform's rules. Returns errors (to fix) and warnings (to weigh). Call it after building and fix every error.",
            """{"type":"object","properties":{}}"""
        ),
        Tool(
            "set_plan",
            "State or revise the plan for the person: the goal, what will be built (in business words), and what only they can provide. It shows the plan, asks the person to confirm (question and options, written in their language) and ENDS your turn: call it before building anything new or any big change, and build only after they answer.",
            """{"type":"object","properties":{"brief":{"type":"string"},"name":{"type":"string","description":"A name for the solution"},"steps":{"type":"array","items":{"type":"string"}},"needs":{"type":"array","items":{"type":"string"}},"builds":{"type":"array","description":"Everything that will exist when the plan is done, one entry each: the team itself as well as each of its agents","items":{"type":"object","properties":{"kind":{"type":"string","enum":["workflow","agent","team"]},"name":{"type":"string"},"tools":{"type":"array","items":{"type":"string"},"description":"For an agent: each tool it gets and why, in a few words of the person's language, e.g. 'Date & Time: to count the 30 days of the returns policy'"}},"required":["kind","name"]}},"question":{"type":"string","description":"The confirmation question, in the person's language"},"options":{"type":"array","items":{"type":"string"},"description":"The answers offered, in the person's language. The first one is the go-ahead, the others ask for a change: e.g. go ahead / change something"}},"required":["brief","steps","builds"]}"""
        ),
        Tool(
            "ask_connection",
            "Ask the person to choose one of their connections. You give the question and the kind; the platform lists every connection of that kind itself and shows them as a choice, so you never write the options. kind \"model\" is the model the agents run on (every provider). The turn ends; their next message names the connection chosen and its id.",
            """{"type":"object","properties":{"question":{"type":"string","description":"In the person's language"},"kind":{"type":"string","description":"model, or a kind of connection such as smtp"}},"required":["question","kind"]}"""
        ),
        Tool(
            "ask_user",
            "Ask the person a question you cannot decide yourself. The turn ends and they answer in their next message. Offer choices when you can.",
            """{"type":"object","properties":{"question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}}},"required":["question"]}"""
        ),
    ];

    public async Task<ToolOutcome> ExecuteAsync(string changesetId, string tool, JsonObject args, CancellationToken cancellationToken = default)
    {
        try
        {
            return tool switch
            {
                "search_capabilities" => await SearchAsync(args, cancellationToken).ConfigureAwait(false),
                "describe_capability" => Describe(args),
                "list_capabilities" => List(args),
                "list_workspace" => await ListWorkspaceAsync(args, cancellationToken).ConfigureAwait(false),
                "list_connections" => await ListConnectionsAsync(args, cancellationToken).ConfigureAwait(false),
                "list_assets" => await ListAssetsAsync(args, cancellationToken).ConfigureAwait(false),
                "get_entity" => await GetEntityAsync(changesetId, args, cancellationToken).ConfigureAwait(false),
                "apply_operations" => await ApplyAsync(changesetId, args, cancellationToken).ConfigureAwait(false),
                "build_from_spec" => await BuildFromSpecAsync(changesetId, args, cancellationToken).ConfigureAwait(false),
                "validate" => await ValidateAsync(changesetId, cancellationToken).ConfigureAwait(false),
                "set_plan" => await SetPlanAsync(changesetId, args, cancellationToken).ConfigureAwait(false),
                "ask_user" => Ask(args),
                "ask_connection" => await AskConnectionAsync(args, cancellationToken).ConfigureAwait(false),
                _ => Failure($"There is no tool named '{tool}'.", "I tried to use something that does not exist."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure($"The tool failed: {ex.Message}", "Something went wrong while doing that.");
        }
    }

    private static ToolOutcome Failure(string forModel, string forPerson) => new() { Ok = false, Content = Cap(JsonText(new JsonObject { ["ok"] = false, ["error"] = forModel })), Summary = forPerson };

    private static string JsonText(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static string Cap(string text) =>
        text.Length <= MaxResultChars ? text : text[..MaxResultChars] + $"… [cut: {text.Length - MaxResultChars} more characters; ask for something narrower]";

    private static string? Text(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    // ── Discovery ─────────────────────────────────────────────────────────────────────────────

    private async Task<ToolOutcome> SearchAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var query = Text(args, "query");
        if (query is null)
            return Failure("'query' is required.", "I did not know what to look for.");

        var limit = args["limit"] is JsonValue l && l.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 15) : 8;
        var found = await registry.SearchAsync(query, Text(args, "kind"), limit, cancellationToken: cancellationToken).ConfigureAwait(false);
        var hits = found.Hits;
        var result = new JsonObject
        {
            ["hits"] = new JsonArray(
                [
                    .. hits.Select(h =>
                        (JsonNode)new JsonObject
                        {
                            ["kind"] = h.Capability.Kind,
                            ["id"] = h.Capability.Id,
                            ["name"] = h.Capability.Name,
                            ["summary"] = CapabilitySnapshot.Summary(h.Capability),
                            ["category"] = h.Capability.Category,
                        }
                    ),
                ]
            ),
        };
        if (hits.Count == 0)
            result["note"] = "Nothing matched. Try other words, or browse with list_capabilities.";
        return new ToolOutcome { Content = Cap(JsonText(result)), Summary = $"Looking for “{query}” — {hits.Count} possibilit{(hits.Count == 1 ? "y" : "ies")}" };
    }

    private ToolOutcome Describe(JsonObject args)
    {
        var kind = Text(args, "kind");
        var id = Text(args, "id");
        if (kind is null || id is null)
            return Failure("'kind' and 'id' are required.", "I did not know what to read about.");

        var item = registry.Current.Get(kind, id);
        if (item is null)
        {
            var near = registry.Current.Search(id.Replace('.', ' '), kind, 4).Select(h => h.Capability.Id);
            return Failure($"There is no {kind} named '{id}'. Closest: {string.Join(", ", near)}.", "That does not exist on this platform.");
        }

        return new ToolOutcome { Content = Cap(CapabilitySnapshot.CardOf(item)), Summary = $"Reading about “{item.Name}”" };
    }

    private ToolOutcome List(JsonObject args)
    {
        var kind = Text(args, "kind");
        if (kind is null)
            return Failure("'kind' is required.", "I did not know what to browse.");

        var items = registry.Current.List(kind, Text(args, "category"));
        var result = new JsonObject
        {
            ["count"] = items.Count,
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["name"] = i.Name, ["summary"] = CapabilitySnapshot.Summary(i), ["category"] = i.Category })]),
        };
        return new ToolOutcome { Content = Cap(JsonText(result)), Summary = $"Browsing {items.Count} {kind.Replace('_', ' ')}(s)" };
    }

    private async Task<ToolOutcome> ListWorkspaceAsync(JsonObject args, CancellationToken ct)
    {
        var refs = await lookup.ListAsync(Text(args, "kind"), Text(args, "query"), 40, ct).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["count"] = refs.Count,
            ["entities"] = new JsonArray([.. refs.Select(e => (JsonNode)new JsonObject { ["kind"] = e.Kind, ["id"] = e.Id, ["name"] = e.Name, ["tags"] = new JsonArray([.. e.Tags.Select(t => (JsonNode)JsonValue.Create(t)!)]) })]),
        };
        return new ToolOutcome { Content = Cap(JsonText(result)), Summary = refs.Count == 0 ? "Checking what already exists — nothing yet" : $"Checking what already exists — {refs.Count} found" };
    }

    private async Task<ToolOutcome> ListConnectionsAsync(JsonObject args, CancellationToken ct)
    {
        // Asking for the connections of one model provider is asking which model to use: the answer is every model connection
        // the person has, whatever its provider. Which providers there are is the platform's knowledge, not the model's: a
        // filter on "openai" would hide the person's Anthropic connection from the choice they are offered.
        var kind = Text(args, "kind");
        var modelKinds = ModelConnections.KindsWithAModelStep(registry.Current);
        var aboutModels = kind is not null && (modelKinds.ContainsKey(kind) || kind.ToLowerInvariant() is "model" or "models" or "llm");
        var found = connections is null ? [] : await connections.ListAsync(aboutModels ? null : kind, ct).ConfigureAwait(false);
        if (aboutModels)
            found = [.. found.Where(c => modelKinds.ContainsKey(c.Kind))];

        var result = new JsonObject
        {
            ["count"] = found.Count,
            ["connections"] = new JsonArray(
                [
                    .. found.Select(c =>
                    {
                        var item = new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["kind"] = c.Kind, ["description"] = c.Description };
                        if (modelKinds.TryGetValue(c.Kind, out var modelStep))
                            item["modelStep"] = modelStep;
                        return (JsonNode)item;
                    }),
                ]
            ),
        };
        if (aboutModels && found.Count > 0)
            result["note"] = "These are all the model connections the person has, of every provider. When you ask which one to use, offer every one of them by name; \"modelStep\" is the type of the model step that goes with each.";
        if (found.Count == 0)
            result["note"] = "The person has none of this kind yet. Do not stop there and do not send them away: call ask_connection with this kind, they can create one from the conversation (now, or a placeholder to set up later), then carry on with it.";
        return new ToolOutcome { Content = Cap(JsonText(result)), Summary = found.Count == 0 ? "Checking your connections — none yet" : $"Checking your connections — {found.Count} found" };
    }

    private async Task<ToolOutcome> ListAssetsAsync(JsonObject args, CancellationToken ct)
    {
        var kind = Text(args, "kind");
        var query = Text(args, "query");
        var found = assets is null ? [] : await assets.ListAsync(kind, ct).ConfigureAwait(false);
        if (query is not null)
            found = [.. found.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (a.Folder?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))];

        var result = new JsonObject
        {
            ["count"] = found.Count,
            ["assets"] = new JsonArray([.. found.Select(a => (JsonNode)new JsonObject { ["kind"] = a.Kind, ["id"] = a.Id, ["name"] = a.Name, ["folder"] = a.Folder, ["holds"] = a.Description })]),
        };
        var sameName = found.GroupBy(a => (a.Kind, a.Name.Trim().ToLowerInvariant())).Where(g => g.Count() > 1).Select(g => g.First().Name).ToList();
        if (sameName.Count > 0)
            result["note"] = $"Several have the same name ({string.Join(", ", sameName)}): unless the person said which, ask the person which one (by its folder) with ask_user.";
        if (found.Count == 0)
            result["note"] = "None found. Do not leave the input empty without saying so, and never invent an id: tell the person and list it under what they need to provide.";
        return new ToolOutcome { Content = Cap(JsonText(result)), Summary = found.Count == 0 ? "Looking at your data — nothing found" : $"Looking at your data — {found.Count} found" };
    }

    private async Task<ToolOutcome> GetEntityAsync(string changesetId, JsonObject args, CancellationToken ct)
    {
        var reference = Text(args, "entity");
        if (reference is null)
            return Failure("'entity' is required.", "I did not know which one to read.");

        var changeset = await store.GetAsync(changesetId, ct).ConfigureAwait(false);
        var local = changeset?.Entities.FirstOrDefault(e => e.State != EntityStates.Deleted && (string.Equals(e.Id, reference, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, reference, StringComparison.OrdinalIgnoreCase)));
        string kind;
        string name;
        string state;
        JsonObject draft;
        if (local is not null)
        {
            (kind, name, state, draft) = (local.Kind, local.Name, local.State, local.Draft);
        }
        else
        {
            var live = await lookup.GetAsync(reference, ct).ConfigureAwait(false) ?? (await lookup.FindByNameAsync(reference, ct).ConfigureAwait(false)).FirstOrDefault();
            if (live is null)
                return Failure($"There is no entity '{reference}' in the changeset or in the workspace.", "I could not find it.");
            (kind, name, state, draft) = (live.Kind, live.Name, "saved", live.Json);
        }

        var full = args["full"] is JsonValue f && f.TryGetValue<bool>(out var wantFull) && wantFull;
        var view = full ? (JsonNode)draft.DeepClone() : Summarise(kind, name, state, draft);
        return new ToolOutcome { Content = Cap(JsonText(view)), Summary = $"Reading “{name}”" };
    }

    private static JsonObject Summarise(string kind, string name, string state, JsonObject draft)
    {
        var view = new JsonObject { ["kind"] = kind, ["name"] = name, ["state"] = state, ["id"] = draft["id"]?.DeepClone() };
        if (kind == EntityKinds.Team)
        {
            view["orchestrator"] = draft["orchestratorId"]?.DeepClone();
            view["goal"] = draft["goal"]?.DeepClone();
            // The manager of a manager-led team is the member at the top of the topology — the first one.
            view["manager"] = string.Equals(draft["orchestratorId"]?.GetValue<string>(), "manager-led", StringComparison.OrdinalIgnoreCase) ? (draft["members"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["topologyPositionId"]?.DeepClone() : null;
            view["members"] = new JsonArray([.. (draft["members"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (JsonNode)new JsonObject { ["position"] = m["topologyPositionId"]?.DeepClone(), ["entity"] = m["memberEntityId"]?.DeepClone(), ["type"] = m["memberType"]?.DeepClone(), ["name"] = m["displayName"]?.DeepClone() })]);
            view["connections"] = new JsonArray([.. (draft["connections"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (JsonNode)$"{c["fromTopologyPositionId"]} -> {c["toTopologyPositionId"]} ({string.Join(",", (c["types"] as JsonArray ?? []).Select(t => t?.ToString()))})")]);
            return view;
        }

        var graph = kind == EntityKinds.Agent ? draft["graph"] as JsonObject ?? [] : draft;
        view["entry"] = graph["entryNode"]?.DeepClone();
        view["nodes"] = new JsonArray(
            [
                .. (graph["nodes"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(n =>
                    {
                        var inputs = new JsonObject();
                        foreach (var (key, value) in n["inputs"] as JsonObject ?? [])
                        {
                            var text = value?.ToJsonString() ?? "null";
                            inputs[key] = text.Length > 160 ? text[..160] + "…" : value?.DeepClone();
                        }

                        return (JsonNode)new JsonObject { ["id"] = n["id"]?.DeepClone(), ["type"] = n["nodeType"]?.DeepClone(), ["inputs"] = inputs };
                    }),
            ]
        );
        view["connections"] = new JsonArray([.. (graph["connections"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (JsonNode)$"{c["from"]}.{c["fromPort"]} -> {c["to"]}.{c["toPort"]}")]);
        return view;
    }

    // ── Work ──────────────────────────────────────────────────────────────────────────────────

    private async Task<ToolOutcome> BuildFromSpecAsync(string changesetId, JsonObject args, CancellationToken ct)
    {
        if (specs is null)
            return Failure("Building from a description is not available here.", "I could not build it that way.");

        // The description arrives as an object, or as text holding one.
        var spec = args["spec"];
        if (spec is JsonValue v && v.TryGetValue<string>(out var text))
        {
            try
            {
                spec = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return Failure("'spec' must be a JSON object.", "I sent something I could not read.");
            }
        }

        if (await PlanGateAsync(changesetId, ct).ConfigureAwait(false) is { } gate)
            return gate;

        var result = await specs.BuildAsync(changesetId, spec, ct).ConfigureAwait(false);
        if (result is null)
            return Failure("This changeset is not open any more.", "The work in progress is closed.");

        var payload = new JsonObject { ["built"] = result.Built };
        if (result.Problems.Count > 0)
        {
            payload["problems"] = new JsonArray([.. result.Problems.Select(p => (JsonNode)new JsonObject { ["where"] = p.Where, ["problem"] = p.Message, ["hint"] = p.Hint, ["options"] = p.Options is null ? null : new JsonArray([.. p.Options.Select(o => (JsonNode)JsonValue.Create(o)!)]) })]);
            payload["note"] = "Nothing was built. Correct every listed problem and send the whole description again.";
        }

        if (result.Notes.Count > 0)
            payload["notes"] = new JsonArray([.. result.Notes.Take(12).Select(n => (JsonNode)JsonValue.Create(n)!)]);
        if (result.Built)
        {
            payload["solution"] = result.Solution?.DeepClone();
            payload["created"] = new JsonArray([.. result.Created]);
            var errors = result.Issues.Where(i => i.Severity == "error").ToList();
            var warnings = result.Issues.Count(i => i.Severity == "warning");
            payload["validation"] = new JsonObject
            {
                ["errors"] = errors.Count,
                ["warnings"] = warnings,
                ["issues"] = JsonSerializer.SerializeToNode(result.Issues.Take(12).Select(i => new { i.Severity, i.Code, i.Message, entity = i.Entity, node = i.NodeId, i.Hint })),
            };
            if (errors.Count > 0)
                payload["note"] = "It is built. What the validation lists still has to be filled in or corrected (set_input, add_resource…).";
        }

        var steps = result.Created.Sum(c => c["steps"]?.GetValue<int>() ?? 0);
        var summary = result.Built
            ? $"Built {string.Join(", ", result.Created.Select(c => $"“{c["name"]}”"))} ({steps} step{(steps == 1 ? "" : "s")})"
            : $"That could not be built: {result.Problems.Count} problem{(result.Problems.Count == 1 ? "" : "s")} — {result.Problems.FirstOrDefault()?.Message}";
        return new ToolOutcome { Ok = result.Built, Content = Cap(JsonText(payload)), Summary = summary, ChangedChangeset = result.Built };
    }

    private async Task<ToolOutcome> ApplyAsync(string changesetId, JsonObject args, CancellationToken ct)
    {
        List<BuilderOperation> operations;
        if (Text(args, "commands") is { } commands)
        {
            var (parsed, error) = CommandParser.Parse(commands);
            if (error is not null)
                return Failure($"Could not read the commands. {error} Write one operation per line: name key=value key=value. Nothing was applied.", $"I sent something I could not read. {error} (It began: {(commands.Length > 160 ? commands[..160] + "…" : commands).ReplaceLineEndings(" ⏎ ")})");
            operations = parsed;
        }
        else if (args["operations"] is JsonArray { Count: > 0 } array)
        {
            operations = [.. array.OfType<JsonObject>().Select(BuilderOperation.From)];
            if (operations.Count != array.Count)
                return Failure("Every operation must be an object with an 'op'.", "I sent something malformed.");
        }
        else
        {
            var shape = args.ToJsonString();
            return Failure("Send 'commands' (one operation per line, as one text) or a non-empty 'operations' list.", $"I did not have anything to do. (It arrived as: {(shape.Length > 200 ? shape[..200] + "…" : shape)})");
        }

        // Unless the model asks for all-or-nothing, what is right is kept and the batch stops at the first refusal: a model that
        // had a whole batch thrown away for one wrong word tends to rebuild everything and lose track of what exists.
        if (operations.Any(o => string.Equals(o.Op, "create_entity", StringComparison.OrdinalIgnoreCase)) && await PlanGateAsync(changesetId, ct).ConfigureAwait(false) is { } gate)
            return gate;

        var atomic = args["atomic"] is JsonValue a && a.TryGetValue<bool>(out var flag) && flag;
        var result = await builder.ApplyOperationsAsync(changesetId, operations, atomic, Text(args, "label"), ct, stopOnRefusal: !atomic).ConfigureAwait(false);
        if (result is null)
            return Failure("This changeset is not open any more.", "The work in progress is closed.");

        var payload = new JsonObject
        {
            ["applied"] = result.Applied,
            ["results"] = new JsonArray(
                [
                    .. result.Results.Select(r =>
                    {
                        var item = new JsonObject { ["op"] = r.Op, ["ok"] = r.Ok, ["message"] = r.Message };
                        if (r.Produced is not null)
                            item["produced"] = r.Produced.DeepClone();
                        if (r.Warnings.Count > 0)
                            item["warnings"] = new JsonArray([.. r.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!)]);
                        if (r.Error is not null)
                            item["error"] = JsonSerializer.SerializeToNode(r.Error);
                        return (JsonNode)item;
                    }),
                ]
            ),
        };
        var refusedAny = result.Results.Any(r => !r.Ok);
        if (!result.Applied && result.Atomic)
            payload["note"] = "Nothing was changed. Correct the refused operation and send the batch again.";
        else if (refusedAny && result.Applied)
            payload["note"] = "The operations before the refused one were applied and are kept. Send only the refused one (corrected) and what came after it; do not repeat what was applied.";
        else if (refusedAny)
            payload["note"] = "Nothing was changed. Correct the refused operation and send it again.";

        var done = result.Results.Where(r => r.Ok).Select(r => r.Message).ToList();
        var refused = result.Results.FirstOrDefault(r => !r.Ok);
        var summary = result.Applied
            ? string.Join(" ", done.Take(4)) + (done.Count > 4 ? $" (and {done.Count - 4} more)" : "")
            : $"That could not be done: {refused?.Message}";
        if (result.Applied && refusedAny)
            summary = string.Join(" ", done.Take(3)) + $" Then it stopped: {refused?.Message}";
        return new ToolOutcome { Ok = result.Applied && !refusedAny, Content = Cap(JsonText(payload)), Summary = summary, ChangedChangeset = result.Applied };
    }

    private async Task<ToolOutcome> ValidateAsync(string changesetId, CancellationToken ct)
    {
        var issues = await builder.ValidateAsync(changesetId, ct).ConfigureAwait(false);
        if (issues is null)
            return Failure("This changeset is not open any more.", "The work in progress is closed.");

        var errors = issues.Count(i => i.Severity == "error");
        var warnings = issues.Count(i => i.Severity == "warning");
        var payload = new JsonObject
        {
            ["valid"] = errors == 0,
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["issues"] = JsonSerializer.SerializeToNode(issues.Select(i => new { i.Severity, i.Code, i.Message, entity = i.Entity, node = i.NodeId, i.Hint })),
        };
        var summary = errors == 0 && warnings == 0 ? "Checked: no problem found" : $"Checked: {errors} problem(s) to fix, {warnings} to weigh";
        return new ToolOutcome { Content = Cap(JsonText(payload)), Summary = summary };
    }

    /// <summary>Null when building may go ahead; otherwise the refusal that tells the model what to do first.</summary>
    private async Task<ToolOutcome?> PlanGateAsync(string changesetId, CancellationToken ct)
    {
        if (!RequirePlanFirst)
            return null;

        var changeset = await store.GetAsync(changesetId, ct).ConfigureAwait(false);
        if (changeset is null)
            return null;

        // What already exists in the changeset is a continuation of work the person has seen: only a first creation needs the plan.
        if (changeset.Plan is { Confirmed: true })
            return null;

        var why = changeset.Plan is null
            ? "No plan has been stated yet."
            : "The plan has not been confirmed by the person yet: they have not answered it.";
        return Failure(
            $"Nothing new is built before the person has read a plan and answered it. {why} Call set_plan now (a few short lines: the goal, what will be built in business words, the choices you made, what you will need from them): it shows the plan, asks the person to confirm and ends your turn. Build only after they have answered.",
            "I need to show you the plan before building."
        );
    }

    /// <summary>
    /// Whether <paramref name="stated"/> is the plan the person agreed to, said again: the same things to build (whatever the
    /// wording around them), or, for a plan that names none, the same goal.
    /// </summary>
    private static bool SamePlan(AssistantPlan agreed, AssistantPlan stated)
    {
        static HashSet<string> Keys(AssistantPlan plan) => plan.Builds.Select(b => $"{b.Kind}:{b.Name.Trim()}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var before = Keys(agreed);
        if (!before.SetEquals(Keys(stated)))
            return false;
        return before.Count > 0 || string.Equals(agreed.Brief.Trim(), stated.Brief.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ToolOutcome> SetPlanAsync(string changesetId, JsonObject args, CancellationToken ct)
    {
        var brief = Text(args, "brief");
        if (brief is null)
            return Failure("'brief' is required.", "I did not have a goal to state.");

        static List<string> Strings(JsonNode? node) => (node as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];

        var builds = (args["builds"] as JsonArray)?.OfType<JsonObject>()
            .Select(b => new PlannedEntity { Kind = (Text(b, "kind") ?? "").ToLowerInvariant(), Name = Text(b, "name") ?? "", Tools = Strings(b["tools"]) })
            .Where(b => b.Kind is EntityKinds.Workflow or EntityKinds.Agent or EntityKinds.Team)
            .ToList() ?? [];
        var plan = new AssistantPlan { Brief = brief, Name = Text(args, "name"), Steps = Strings(args["steps"]), Needs = Strings(args["needs"]), Builds = builds };
        var changeset = await store.GetAsync(changesetId, ct).ConfigureAwait(false);
        if (changeset is null)
            return Failure("This changeset does not exist.", "The work in progress is missing.");

        // The person said go ahead and nothing is built yet: whatever the plan stated now says, they are not asked a second
        // time. A model that states its plan again instead of building would otherwise keep them answering the same question.
        if (changeset.Plan is { Confirmed: true, GoAhead: true } && !changeset.Entities.Any(e => e.State != EntityStates.Deleted))
        {
            return new ToolOutcome
            {
                Content = JsonText(new JsonObject { ["ok"] = true, ["alreadyConfirmed"] = true, ["note"] = "The person has just agreed to the plan: it was not shown again and nothing was asked. Do not call set_plan again. Build what was agreed now (build_from_spec), and keep going until it is done. If something truly prevents it, say what with ask_user." }),
                Summary = "The plan is already agreed",
            };
        }

        // A plan is what the person reads before anything is built, and what the work is checked against afterwards: a goal
        // alone is neither.
        if (plan.Steps.Count == 0 || plan.Builds.Count == 0)
            return Failure("A plan needs 'steps' (what will be built, in a few lines of business words) and 'builds' (everything that will exist at the end, each with its kind and name: the team itself as well as each of its agents). Call set_plan again with both.", "My plan did not say what it builds.");

        // The person has already said yes to this plan: stating it again, in other words, would ask them the same question
        // twice. It is not shown and not asked again; the model is told to get on with it.
        if (changeset.Plan is { Confirmed: true } agreed && SamePlan(agreed, plan))
        {
            return new ToolOutcome
            {
                Content = JsonText(new JsonObject { ["ok"] = true, ["alreadyConfirmed"] = true, ["note"] = "The person has already read this plan and agreed to it: it was not shown again and nothing was asked. Do not state it again. Build it now, and keep going until it is done." }),
                Summary = "The plan is already agreed",
            };
        }

        changeset.Plan = plan;
        if (string.IsNullOrWhiteSpace(changeset.Name) && plan.Name is { } name)
            changeset.Name = name;
        await store.SaveAsync(changeset, ct).ConfigureAwait(false);
        // The plan is put to the person together with its confirmation, and the turn ends there: they read it before anything
        // is built. The wording of the question is the model's (in the person's language); the choices default to two.
        var question = Text(args, "question") ?? "Shall I go ahead with this plan?";
        var options = (args["options"] as JsonArray)?.Select(o => o?.ToString() ?? "").Where(o => !string.IsNullOrWhiteSpace(o)).Take(4).ToList() is { Count: > 0 } given ? given : ["Go ahead", "Change something"];
        plan.Options = [.. options];
        await store.SaveAsync(changeset, ct).ConfigureAwait(false);
        return new ToolOutcome
        {
            Content = JsonText(new JsonObject { ["ok"] = true, ["note"] = "The plan was shown to the person, who is now asked to confirm. The turn ends here." }),
            Summary = "Drafting the plan",
            Plan = plan,
            Question = new Question(question, options),
        };
    }

    /// <summary>
    /// A question whose answer is one of the person's connections. The model says what it needs; what there is to choose
    /// among is read from the platform, so no connection can be forgotten, renamed or made up on the way.
    /// </summary>
    private async Task<ToolOutcome> AskConnectionAsync(JsonObject args, CancellationToken ct)
    {
        var question = Text(args, "question");
        var kind = Text(args, "kind");
        if (question is null || kind is null)
            return Failure("'question' and 'kind' are required.", "I did not know what to ask for.");

        var modelKinds = ModelConnections.KindsWithAModelStep(registry.Current);
        var aboutModels = modelKinds.ContainsKey(kind) || kind.ToLowerInvariant() is "model" or "models" or "llm";
        var found = connections is null ? [] : await connections.ListAsync(aboutModels ? null : kind, ct).ConfigureAwait(false);
        if (aboutModels)
            found = [.. found.Where(c => modelKinds.ContainsKey(c.Kind))];

        // What the person can create on the spot when they have none, or want another: the kinds that answer the question.
        var kinds = connections?.ListKinds() ?? [];
        List<ConnectionKindOption> creatable =
        [
            .. kinds
                .Where(k => aboutModels ? modelKinds.ContainsKey(k.Id) : string.Equals(k.Id, kind, StringComparison.OrdinalIgnoreCase))
                .Select(k => new ConnectionKindOption { Id = k.Id, Name = k.Name }),
        ];

        // Nothing to choose among and nothing that can be created is not a question: the model tells the person.
        if (found.Count == 0 && creatable.Count == 0)
            return new ToolOutcome { Content = JsonText(new JsonObject { ["ok"] = true, ["count"] = 0, ["note"] = "The person has no connection of this kind and the platform does not know how to create one: nothing was asked. Say what they will need, and go on with what does not depend on it." }), Summary = "Checking your connections — none yet" };

        var pick = new ConnectionChoice
        {
            Kind = aboutModels ? "model" : kind,
            Purpose = aboutModels ? "agent_model" : "connection",
            Connections = [.. found.Select(c => new ConnectionOption { Id = c.Id, Name = c.Name, Kind = c.Kind, Description = c.Description })],
            Creatable = creatable,
        };
        return new ToolOutcome
        {
            Content = JsonText(new JsonObject { ["ok"] = true, ["count"] = found.Count, ["note"] = found.Count == 0 ? "The person has none yet: they are offered to create one now, or a placeholder to set up later. The turn ends here; their answer names the connection to use and its id." : "The person is shown their connections and asked to choose (they can also create a new one). The turn ends here." }),
            Summary = question,
            Question = new Question(question, [.. found.Select(c => c.Name)], pick),
        };
    }

    private static ToolOutcome Ask(JsonObject args)
    {
        var question = Text(args, "question");
        if (question is null)
            return Failure("'question' is required.", "I did not have a question to ask.");

        var options = (args["options"] as JsonArray)?.Select(o => o?.ToString() ?? "").Where(o => o.Length > 0).ToList() ?? [];
        return new ToolOutcome { Content = JsonText(new JsonObject { ["ok"] = true }), Summary = question, Question = new Question(question, options) };
    }
}
