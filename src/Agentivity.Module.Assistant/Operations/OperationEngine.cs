using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Module.Assistant.Changesets;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>
/// Applies operations to the drafts of a changeset. It knows the general shape of a workflow, an agent and a team; what can
/// fill that shape (which nodes, which inputs, which ports, which orchestrators) is read from the capability registry at the
/// moment of the call, so a node added by a module is usable without a change here.
/// </summary>
public sealed partial class OperationEngine(ICapabilityRegistry registry, IEntityLookup lookup)
{
    private const int MaxRevisions = 20;
    private const int GridStepX = 280;
    private const int GridStartX = 120;
    private const int GridStartY = 140;

    /// <summary>The names an operation can take, in the order they are documented.</summary>
    public static readonly IReadOnlyList<string> Operations =
    [
        "create_entity", "rename_entity", "set_tags", "delete_entity",
        "add_node", "add_resource", "complete_resources", "set_input", "remove_input", "connect", "disconnect", "move_node", "remove_node", "set_entry",
        "set_orchestrator", "add_member", "set_member_group", "remove_member", "set_manager", "set_goal", "connect_members", "disconnect_members",
        "open_entity", "set_root", "set_folder",
    ];

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][\w-]*)\.([A-Za-z_][\w.\-\[\]]*)\s*\}\}")]
    private static partial Regex NodeReference();

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex NotIdentifier();

    private sealed class Work(List<ChangesetEntity> entities, string? rootEntityId, string? folderId, AgentModelChoice? agentModel = null)
    {
        /// <summary>The connection the person chose for the agents built here, if any.</summary>
        public AgentModelChoice? AgentModel { get; } = agentModel;

        public List<ChangesetEntity> Entities { get; } = entities;

        public string? RootEntityId { get; set; } = rootEntityId;

        public string? FolderId { get; set; } = folderId;

        public ChangesetEntity? ById(string id) => Entities.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Refusal(OperationError error) : Exception(error.Message)
    {
        public OperationError Error { get; } = error;
    }

    /// <summary>
    /// Applies a batch. When <paramref name="atomic"/> is true (the default) one refused operation keeps the whole batch out,
    /// so the caller can correct it and send it again; otherwise the accepted operations are kept and the others reported.
    /// </summary>
    public async Task<BatchResult> ApplyAsync(Changeset changeset, IReadOnlyList<BuilderOperation> operations, bool atomic = true, string? label = null, CancellationToken cancellationToken = default, bool stopOnRefusal = false)
    {
        ArgumentNullException.ThrowIfNull(changeset);
        ArgumentNullException.ThrowIfNull(operations);

        var snapshot = registry.Current;
        var work = new Work(changeset.CloneEntities(), changeset.RootEntityId, changeset.FolderId, changeset.AgentModel);
        var results = new List<OperationOutcome>();

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            OperationOutcome result;
            try
            {
                result = await ExecuteAsync(i, op, work, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Refusal refusal)
            {
                result = new OperationOutcome { Index = i, Op = op.Op, Ok = false, Message = refusal.Error.Message, Error = refusal.Error };
            }

            results.Add(result);
            if (!result.Ok && atomic)
                break;
            if (!result.Ok && stopOnRefusal)
            {
                // What comes after may depend on what was refused: it is not run, and said so, rather than failing for a
                // misleading reason. What came before is kept.
                for (var j = i + 1; j < operations.Count; j++)
                    results.Add(new OperationOutcome { Index = j, Op = operations[j].Op, Ok = false, Message = "Not run: an earlier operation in this batch was refused." });
                break;
            }
        }

        var anyOk = results.Any(r => r.Ok);
        var keep = anyOk && (!atomic || results.All(r => r.Ok) && results.Count == operations.Count);
        if (!keep)
            return new BatchResult { Applied = false, Atomic = atomic, Revision = changeset.Revision, Results = results };

        changeset.Revisions.Add(
            new RevisionSnapshot
            {
                Revision = changeset.Revision,
                Label = label ?? string.Join(", ", results.Where(r => r.Ok).Select(r => r.Op).Distinct()),
                Entities = changeset.Entities,
                RootEntityId = changeset.RootEntityId,
            }
        );
        if (changeset.Revisions.Count > MaxRevisions)
            changeset.Revisions.RemoveRange(0, changeset.Revisions.Count - MaxRevisions);

        changeset.Revision++;
        changeset.Entities = work.Entities;
        changeset.RootEntityId = work.RootEntityId;
        changeset.FolderId = work.FolderId;
        changeset.RegistryVersion = snapshot.Version;
        foreach (var ok in results.Where(r => r.Ok))
            changeset.Journal.Add(new JournalEntry { Revision = changeset.Revision, Op = ok.Op, Message = ok.Message });

        if (string.IsNullOrWhiteSpace(changeset.Name) && changeset.RootEntityId is { } rootId && changeset.Find(rootId) is { } root)
            changeset.Name = root.Name;

        return new BatchResult { Applied = true, Atomic = atomic, Revision = changeset.Revision, Results = results };
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────────────────────

    private async Task<OperationOutcome> ExecuteAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var name = (op.Op ?? "").Trim().ToLowerInvariant();
        return name switch
        {
            "create_entity" => await CreateEntityAsync(index, op, work, ct).ConfigureAwait(false),
            "rename_entity" => await RenameEntityAsync(index, op, work, ct).ConfigureAwait(false),
            "set_tags" => await SetTagsAsync(index, op, work, ct).ConfigureAwait(false),
            "delete_entity" => await DeleteEntityAsync(index, op, work, ct).ConfigureAwait(false),
            "add_node" => await AddNodeAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "add_resource" => await AddResourceAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "complete_resources" => await CompleteResourcesAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "set_input" => await SetInputAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "remove_input" => await RemoveInputAsync(index, op, work, ct).ConfigureAwait(false),
            "connect" => await ConnectAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "disconnect" => await DisconnectAsync(index, op, work, ct).ConfigureAwait(false),
            "move_node" => await MoveNodeAsync(index, op, work, ct).ConfigureAwait(false),
            "remove_node" => await RemoveNodeAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "set_entry" => await SetEntryAsync(index, op, work, ct).ConfigureAwait(false),
            "set_orchestrator" => await SetOrchestratorAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "add_member" => await AddMemberAsync(index, op, work, ct).ConfigureAwait(false),
            "set_member_group" => await SetMemberGroupAsync(index, op, work, ct).ConfigureAwait(false),
            "remove_member" => await RemoveMemberAsync(index, op, work, ct).ConfigureAwait(false),
            "set_manager" => await SetManagerAsync(index, op, work, ct).ConfigureAwait(false),
            "set_goal" => await SetGoalAsync(index, op, work, ct).ConfigureAwait(false),
            "connect_members" => await ConnectMembersAsync(index, op, work, snapshot, ct).ConfigureAwait(false),
            "disconnect_members" => await DisconnectMembersAsync(index, op, work, ct).ConfigureAwait(false),
            "open_entity" => await OpenEntityAsync(index, op, work, ct).ConfigureAwait(false),
            "set_root" => await SetRootAsync(index, op, work, ct).ConfigureAwait(false),
            "set_folder" => SetFolder(index, op, work),
            "" => throw new Refusal(new OperationError("missing_op", "An operation needs a name in 'op'.", "Use one of the supported operations.", Operations)),
            _ => throw new Refusal(UnknownOp(op.Op ?? "")),
        };
    }

    /// <summary>
    /// A refusal for an 'op' that is not an operation. When the value is the name followed by stray text (the model mixed quote
    /// styles and ran the rest of the object into the string), say exactly that and show a well-formed one: echoing the garbage
    /// back gave the model nothing to correct and it repeated itself.
    /// </summary>
    private static OperationError UnknownOp(string value)
    {
        var lead = new string(value.Trim().TakeWhile(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();
        var known = Operations.FirstOrDefault(o => string.Equals(o, lead, StringComparison.OrdinalIgnoreCase));
        if (known is not null && lead.Length < value.Trim().Length)
        {
            return new OperationError(
                "malformed_op",
                $"The value of 'op' must be only the operation name, \"{known}\". It was followed by extra text, so the JSON of this operation is malformed: every key and value must use double quotes and the 'op' string must close before the next key.",
                $"Send each operation as its own object, for example {{\"op\":\"{known}\", ...its arguments as separate keys...}}. Nothing was applied.",
                Operations
            );
        }

        var shown = value.Length > 60 ? value[..60] + "…" : value;
        return new OperationError("unknown_op", $"'{shown}' is not an operation.", "Use one of the supported operations.", Operations);
    }

    private static OperationOutcome Ok(int index, BuilderOperation op, string message, JsonObject? produced = null, IEnumerable<string>? warnings = null) =>
        new()
        {
            Index = index,
            Op = op.Op,
            Ok = true,
            Message = message,
            Produced = produced,
            Warnings = [.. warnings ?? []],
        };

    private static Refusal Refuse(string code, string message, string? hint = null, IReadOnlyList<string>? options = null) => new(new OperationError(code, message, hint, options));

    private static string Require(BuilderOperation op, string arg) => op.Text(arg) ?? throw Refuse("missing_argument", $"'{op.Op}' needs '{arg}'.", $"Add \"{arg}\" to the operation.");

    // ── Entities ──────────────────────────────────────────────────────────────────────────────

    private async Task<OperationOutcome> CreateEntityAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var kind = Require(op, "kind").ToLowerInvariant();
        if (!EntityKinds.IsKnown(kind))
            throw Refuse("unknown_kind", $"'{kind}' is not something that can be created.", "Create a workflow, an agent or a team.", [EntityKinds.Workflow, EntityKinds.Agent, EntityKinds.Team]);

        var name = Require(op, "name");
        var warnings = new List<string>();
        if (work.Entities.Any(e => e.State != EntityStates.Deleted && e.Kind == kind && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"Another {kind} in this changeset is already named '{name}'.");
        else if ((await lookup.FindByNameAsync(name, ct).ConfigureAwait(false)).Any(l => l.Kind == kind))
            warnings.Add($"A saved {kind} is already named '{name}'; both will exist.");

        var id = Guid.NewGuid().ToString();
        var draft = kind switch
        {
            EntityKinds.Workflow => new JsonObject { ["id"] = id, ["name"] = name, ["role"] = "", ["entryNode"] = "", ["nodes"] = new JsonArray(), ["connections"] = new JsonArray(), ["metadata"] = new JsonObject() },
            EntityKinds.Agent => new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["graph"] = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = name, ["role"] = "", ["entryNode"] = "", ["nodes"] = new JsonArray(), ["connections"] = new JsonArray(), ["metadata"] = new JsonObject() },
            },
            _ => new JsonObject { ["id"] = id, ["name"] = name, ["orchestratorId"] = "", ["members"] = new JsonArray(), ["connections"] = new JsonArray() },
        };
        if (op.Text("role") is { } role)
            draft["role"] = role;

        work.Entities.Add(new ChangesetEntity { Id = id, Kind = kind, Name = name, State = EntityStates.Created, Draft = draft });
        // The main entity of a solution is what holds the rest together: a team over its agents, a workflow over an agent.
        // The first thing created is not it when something of a higher level comes after (agents first, then their team).
        var explicitRoot = op.Args["root"] is JsonValue r && r.TryGetValue<bool>(out var asRoot) && asRoot;
        var currentRoot = work.RootEntityId is { } rootId ? work.ById(rootId) : null;
        if (explicitRoot || currentRoot is null || Level(kind) > Level(currentRoot.Kind))
            work.RootEntityId = id;

        return Ok(index, op, $"Created {kind} '{name}'.", new JsonObject { ["entityId"] = id, ["kind"] = kind }, warnings);
    }

    private async Task<OperationOutcome> RenameEntityAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var name = Require(op, "name");
        entity.Name = name;
        entity.Draft["name"] = name;
        if (entity.Kind == EntityKinds.Agent && entity.Draft["graph"] is JsonObject graph)
            graph["name"] = name;
        return Ok(index, op, $"Renamed the {entity.Kind} to '{name}'.");
    }

    private async Task<OperationOutcome> SetTagsAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        if (op.Args["tags"] is not JsonArray tags || tags.Any(t => t is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw Refuse("invalid_value", "'tags' must be a list of texts.", "For example [\"finance\", \"invoices\"].");
        entity.Draft["tags"] = tags.DeepClone();
        return Ok(index, op, $"Set {tags.Count} tag(s) on '{entity.Name}'.");
    }

    private async Task<OperationOutcome> DeleteEntityAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var warnings = work.Entities.Where(e => e.Id != entity.Id && e.State != EntityStates.Deleted && e.Draft.ToJsonString().Contains(entity.Id, StringComparison.OrdinalIgnoreCase)).Select(e => $"'{e.Name}' still refers to '{entity.Name}'.").ToList();

        if (entity.State == EntityStates.Created)
            work.Entities.Remove(entity);
        else
        {
            entity.State = EntityStates.Deleted;
            entity.Draft = new JsonObject { ["id"] = entity.Id, ["name"] = entity.Name };
        }

        if (string.Equals(work.RootEntityId, entity.Id, StringComparison.OrdinalIgnoreCase))
            work.RootEntityId = null;
        return Ok(index, op, $"Deleted the {entity.Kind} '{entity.Name}' (nothing is removed from the workspace until the changeset is saved).", warnings: warnings);
    }

    /// <summary>
    /// Brings a saved entity into the work, with everything it holds: the members of a team, and theirs, and what the
    /// steps of a graph call. It becomes the main entity. Nothing is changed by opening: what is not touched afterwards is
    /// not written back when the work is saved.
    /// </summary>
    private async Task<OperationOutcome> OpenEntityAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ChangesetEntity>();
        queue.Enqueue(entity);
        while (queue.Count > 0 && seen.Count < 200)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Id) || current.State == EntityStates.Deleted)
                continue;

            foreach (var reference in References(current))
            {
                // A reference that is not an entity (any other id-like value) is simply not one: nothing to bring in.
                if (work.ById(reference) is { } known)
                {
                    queue.Enqueue(known);
                    continue;
                }

                if (await lookup.GetAsync(reference, ct).ConfigureAwait(false) is null)
                    continue;
                queue.Enqueue(await FindEntityAsync(reference, work, ct).ConfigureAwait(false));
            }
        }

        work.RootEntityId = entity.Id;
        var others = seen.Count - 1;
        return Ok(index, op, others == 0 ? $"Opened the {entity.Kind} '{entity.Name}'." : $"Opened the {entity.Kind} '{entity.Name}' with the {others} thing{(others == 1 ? "" : "s")} it holds.", new JsonObject { ["id"] = entity.Id, ["held"] = others });
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{15,63}$")]
    private static partial Regex LooksLikeAnId();

    /// <summary>What an entity may refer to: the members of a team, and the values of a graph's steps that look like ids.</summary>
    private static IEnumerable<string> References(ChangesetEntity entity)
    {
        if (entity.Kind == EntityKinds.Team)
        {
            foreach (var member in (entity.Draft["members"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (member["memberEntityId"] is JsonValue m && m.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
                    yield return id;
            }

            yield break;
        }

        var graph = entity.Draft["graph"] as JsonObject ?? entity.Draft;
        foreach (var node in (graph["nodes"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            foreach (var input in (node["inputs"] as JsonObject)?.ToList() ?? [])
            {
                if (input.Value is JsonValue v && v.TryGetValue<string>(out var text) && LooksLikeAnId().IsMatch(text) && !string.Equals(text, entity.Id, StringComparison.OrdinalIgnoreCase))
                    yield return text;
            }
        }
    }

    private async Task<OperationOutcome> SetRootAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        work.RootEntityId = entity.Id;
        return Ok(index, op, $"'{entity.Name}' is now the main {entity.Kind} of the solution.");
    }

    /// <summary>How high a kind sits in a solution: a team contains agents and workflows, a workflow can call agents.</summary>
    private static int Level(string kind) =>
        kind switch
        {
            EntityKinds.Team => 3,
            EntityKinds.Workflow => 2,
            _ => 1,
        };

    private static OperationOutcome SetFolder(int index, BuilderOperation op, Work work)
    {
        work.FolderId = op.Text("folder");
        return Ok(index, op, work.FolderId is null ? "New entities will be saved at the root of the workspace." : "New entities will be saved in the chosen folder.");
    }

    // ── Graph (workflow and agent) ────────────────────────────────────────────────────────────

    private static JsonObject GraphOf(ChangesetEntity entity, string op)
    {
        JsonObject? graph = entity.Kind switch
        {
            EntityKinds.Workflow => entity.Draft,
            EntityKinds.Agent => entity.Draft["graph"] as JsonObject,
            _ => null,
        };
        if (graph is null)
            throw Refuse("wrong_kind", $"'{op}' works on a workflow or an agent, not on the {entity.Kind} '{entity.Name}'.", entity.Kind == EntityKinds.Team ? "A team is built with add_member, connect_members and set_orchestrator." : null);

        if (graph["nodes"] is not JsonArray)
            graph["nodes"] = new JsonArray();
        if (graph["connections"] is not JsonArray)
            graph["connections"] = new JsonArray();
        return graph;
    }

    private static JsonArray Nodes(JsonObject graph) => (JsonArray)graph["nodes"]!;

    private static JsonArray Connections(JsonObject graph) => (JsonArray)graph["connections"]!;

    /// <summary>
    /// A node by its id. A model often names a node by its type ("ai.llm.model.openai") or its label instead of the id it was
    /// given; when exactly one node matches that way, it is the one meant.
    /// </summary>
    private static JsonObject? FindNode(JsonObject graph, string id)
    {
        var nodes = Nodes(graph).OfType<JsonObject>().ToList();
        var byId = nodes.FirstOrDefault(n => string.Equals(n["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;

        var byType = nodes.Where(n => string.Equals(n["nodeType"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byType.Count == 1)
            return byType[0];

        var byLabel = nodes.Where(n => string.Equals(n["label"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)).ToList();
        return byLabel.Count == 1 ? byLabel[0] : null;
    }

    private static JsonObject NodeOrRefuse(JsonObject graph, string id, ChangesetEntity entity)
    {
        if (FindNode(graph, id) is { } found)
            return found;

        var looksLikeAType = id.Contains('.') && !id.StartsWith("node_", StringComparison.OrdinalIgnoreCase);
        throw Refuse(
            "unknown_node",
            $"There is no node '{id}' in '{entity.Name}'.",
            looksLikeAType
                ? $"'{id}' looks like a node type, and no node of that type has been added to '{entity.Name}' yet. Add it first with add_node type={id}; the result gives its id (like node_2), then use that id."
                : "Use the id returned when the node was added.",
            [.. Nodes(graph).OfType<JsonObject>().Select(n => n["id"]!.GetValue<string>())]
        );
    }

    private static JsonObject DescriptorOrRefuse(CapabilitySnapshot snapshot, string type, bool suggest = true)
    {
        var item = snapshot.Get(CapabilityKinds.Node, type);
        if (item is not null)
            return item.Details;

        var hits = suggest ? snapshot.Search(type.Replace('.', ' '), CapabilityKinds.Node, 5) : [];
        var options = hits.Select(h => h.Capability.Id).ToList();

        // The closest ones come with what they are, so that one is chosen without another round trip: a step of the flow
        // (add_node) or a resource a step uses (add_resource), and what it does.
        var described = string.Join("; ", hits.Select(h => $"{h.Capability.Id} [{(h.Capability.Details["nodeClass"]?.GetValue<string>() == "resource" ? "resource: add_resource" : "step: add_node")}] {CapabilitySnapshot.Summary(h.Capability)}"));
        throw Refuse(
            "unknown_node_type",
            options.Count > 0 ? $"There is no node type '{type}' in this platform. Closest: {described}." : $"There is no node type '{type}' in this platform.",
            options.Count > 0 ? "Use one of these ids as it is, or search_capabilities for another need." : "Use search_capabilities to find the right node.",
            options
        );
    }

    private Task<OperationOutcome> AddNodeAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct) =>
        AddNodeCoreAsync(index, op, work, snapshot, asResource: false, ct);

    /// <summary>
    /// Adds what a step uses (a model, a memory, a session, a channel, a tool) and connects it to that step. A resource is not
    /// a step of the flow: it has no place in the sequence and its connection is always resource to consumer, so the port and
    /// the direction are found here and not left to the author.
    /// </summary>
    private async Task<OperationOutcome> AddResourceAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var target = Require(op, "to");
        var consumer = NodeOrRefuse(graph, target, entity);

        var added = await AddNodeCoreAsync(index, op, work, snapshot, asResource: true, ct).ConfigureAwait(false);
        var newId = added.Produced!["nodeId"]!.GetValue<string>();
        var newNode = FindNode(graph, newId)!;

        var connect = new JsonObject { ["entity"] = entity.Id, ["from"] = newId, ["to"] = consumer["id"]!.GetValue<string>() };
        if (op.Text("port") is { } port)
            connect["toPort"] = port;
        try
        {
            var connected = await ConnectAsync(index, new BuilderOperation("connect", connect), work, snapshot, ct).ConfigureAwait(false);
            var warnings = added.Warnings.Concat(connected.Warnings).ToList();
            return Ok(index, op, $"Added {op.Text("type")} as '{newId}' and connected it to '{consumer["id"]!.GetValue<string>()}'.", added.Produced, warnings);
        }
        catch (Refusal)
        {
            // The resource is not left behind unconnected when it cannot be plugged where it was asked.
            Nodes(graph).Remove(newNode);
            throw;
        }
    }

    /// <summary>
    /// The resources pass: every step that needs a resource and has none gets one, when there is no doubt about which. An
    /// existing compatible resource is shared; a model uses the connection the person chose; when several kinds fit, the
    /// choices are reported and nothing is guessed.
    /// </summary>
    private async Task<OperationOutcome> CompleteResourcesAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var only = op.Text("node") is { } nodeRef ? NodeOrRefuse(graph, nodeRef, entity)["id"]!.GetValue<string>() : null;
        var done = new List<string>();
        var open = new List<string>();

        foreach (var consumer in Nodes(graph).OfType<JsonObject>().ToList())
        {
            var consumerId = consumer["id"]!.GetValue<string>();
            if (only is not null && !string.Equals(only, consumerId, StringComparison.OrdinalIgnoreCase))
                continue;
            var details = snapshot.Get(CapabilityKinds.Node, consumer["nodeType"]!.GetValue<string>())?.Details;
            if (details is null)
                continue;

            foreach (var port in Ports(details, "inputPorts").Where(p => PortKind(p) == "resource" && p["required"]?.GetValue<bool>() == true))
            {
                var portName = port["name"]!.GetValue<string>();
                var resourceType = port["resourceType"]?.GetValue<string>();
                if (Connections(graph).OfType<JsonObject>().Any(c => string.Equals(c["to"]?.GetValue<string>(), consumerId, StringComparison.OrdinalIgnoreCase) && string.Equals(c["toPort"]?.GetValue<string>(), portName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // One that is already in the draft is shared before another one is made.
                var existing = Nodes(graph).OfType<JsonObject>().FirstOrDefault(n =>
                    !ReferenceEquals(n, consumer)
                    && snapshot.Get(CapabilityKinds.Node, n["nodeType"]!.GetValue<string>())?.Details is { } d
                    && d["nodeClass"]?.GetValue<string>() == "resource"
                    && Ports(d, "outputPorts").Any(o => PortKind(o) == "resource" && Compatible(o["resourceType"]?.GetValue<string>(), resourceType))
                );
                if (existing is not null)
                {
                    var link = new BuilderOperation("connect", new JsonObject { ["entity"] = entity.Id, ["from"] = existing["id"]!.GetValue<string>(), ["to"] = consumerId, ["toPort"] = portName });
                    done.Add((await ConnectAsync(index, link, work, snapshot, ct).ConfigureAwait(false)).Message);
                    continue;
                }

                var candidates = ResourceTypesFor(snapshot, resourceType);
                string? pick = null;
                if (work.AgentModel is { Kind.Length: > 0 } chosen && resourceType?.StartsWith("llm.model", StringComparison.OrdinalIgnoreCase) == true)
                    pick = ModelConnections.KindsWithAModelStep(snapshot).GetValueOrDefault(chosen.Kind);
                if (pick is null && candidates.Count == 1)
                    pick = candidates[0];
                if (pick is null)
                {
                    open.Add($"'{consumerId}' needs {resourceType ?? "a resource"} on '{portName}': choose one of {string.Join(", ", candidates.Take(8))} and add it with add_resource.");
                    continue;
                }

                var add = new BuilderOperation("add_resource", new JsonObject { ["entity"] = entity.Id, ["to"] = consumerId, ["type"] = pick, ["port"] = portName });
                done.Add((await AddResourceAsync(index, add, work, snapshot, ct).ConfigureAwait(false)).Message);
            }
        }

        var message = done.Count == 0 && open.Count == 0 ? "Every step already has the resources it needs." : string.Join(" ", done);
        if (open.Count > 0)
            message += (message.Length > 0 ? " " : "") + "Still to decide: " + string.Join(" ", open);
        return Ok(index, op, message.Trim(), warnings: open);
    }

    /// <summary>The resource steps that can provide a resource of this type, read from the platform.</summary>
    private static List<string> ResourceTypesFor(CapabilitySnapshot snapshot, string? resourceType) =>
        [
            .. snapshot
                .List(CapabilityKinds.Node)
                .Where(n => n.Details["nodeClass"]?.GetValue<string>() == "resource" && Ports(n.Details, "outputPorts").Any(o => PortKind(o) == "resource" && !string.IsNullOrWhiteSpace(resourceType) && Compatible(o["resourceType"]?.GetValue<string>(), resourceType)))
                .Select(n => n.Id)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];

    private async Task<OperationOutcome> AddNodeCoreAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, bool asResource, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var requestedType = Require(op, "type");

        // A type that does not exist is replaced by the one that was plainly meant (same family), and that is said; when there
        // is doubt it is refused with the closest ones described.
        var type = requestedType;
        string? resolvedNote = null;
        var known = snapshot.Get(CapabilityKinds.Node, requestedType);
        if (known is not null)
        {
            type = known.Id; // the platform's own spelling (the lookup ignores case)
        }
        else if (TypeResolver.Resolve(snapshot, requestedType, asResource) is { } meant)
        {
            type = meant;
            resolvedNote = $"'{requestedType}' is not a type of this platform; I used '{meant}'.";
        }

        var details = DescriptorOrRefuse(snapshot, type);

        var isResource = string.Equals(details["nodeClass"]?.GetValue<string>(), "resource", StringComparison.OrdinalIgnoreCase);
        if (isResource && !asResource)
            throw Refuse("use_add_resource", $"'{type}' is a resource: it supplies a model, a memory, a tool or a channel to a step, it is not a step of the flow.", $"Use add_resource to=<the step that uses it> type={type}; it is connected for you.");
        if (!isResource && asResource)
            throw Refuse("use_add_node", $"'{type}' is a step of the flow, not a resource.", "Use add_node for a step. add_resource is for what a step uses: a model, a memory, a session, a channel, a tool.");

        string id;
        if (op.Text("id") is { } requested)
        {
            if (NotIdentifier().IsMatch(requested))
                throw Refuse("invalid_id", $"'{requested}' is not a valid node id.", "Use letters, digits, '-' and '_'.");
            if (FindNode(graph, requested) is not null)
                throw Refuse("duplicate_id", $"A node '{requested}' already exists in '{entity.Name}'.", "Choose another id, or leave it out to get one.");
            id = requested;
        }
        else
        {
            var stem = NotIdentifier().Replace(type.Split('.').Last(), "_").Trim('_');
            if (stem.Length == 0)
                stem = "node";
            var n = 1;
            while (FindNode(graph, $"{stem}_{n}") is not null)
                n++;
            id = $"{stem}_{n}";
        }

        var count = Nodes(graph).Count;
        var position = op.Args["position"] as JsonObject;
        var node = new JsonObject
        {
            ["id"] = id,
            ["nodeType"] = type,
            ["position"] = new JsonObject
            {
                ["x"] = position?["x"]?.DeepClone() ?? GridStartX + count * GridStepX,
                ["y"] = position?["y"]?.DeepClone() ?? GridStartY,
            },
            ["inputs"] = new JsonObject(),
            ["inputsMetadata"] = new JsonObject(),
            ["metadata"] = new JsonObject(),
        };
        if (op.Text("label") is { } label)
            ((JsonObject)node["metadata"]!)["displayName"] = label;

        var warnings = new List<string>();
        if (resolvedNote is not null)
            warnings.Add(resolvedNote);
        if (op.Args["inputs"] is JsonObject inputs)
        {
            foreach (var (inputName, value) in inputs)
                warnings.AddRange(ApplyInput(graph, node, details, inputName, value, entity));
        }

        // A model step takes the connection the person chose for their agents, unless one was given: the choice is made once.
        if (work.AgentModel is { Kind.Length: > 0 } chosen)
        {
            var credentialInput = (details["inputs"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(i => string.Equals(i["credentialsTemplateId"]?.GetValue<string>(), chosen.Kind, StringComparison.OrdinalIgnoreCase));
            var inputName = credentialInput?["name"]?.GetValue<string>();
            if (inputName is not null && (node["inputs"] as JsonObject)?[inputName] is null)
            {
                warnings.AddRange(ApplyInput(graph, node, details, inputName, JsonValue.Create(chosen.ConnectionId), entity));
                warnings.Add($"Used the connection you chose for your agents ({chosen.Name}).");
            }
        }

        Nodes(graph).Add(node);

        var nodeClass = details["nodeClass"]?.GetValue<string>();
        var note = "";
        if (string.IsNullOrWhiteSpace(graph["entryNode"]?.GetValue<string>()) && nodeClass != "resource")
        {
            graph["entryNode"] = id;
            note = " It is the entry point of the graph.";
        }

        return Ok(index, op, $"Added {type} as '{id}' to '{entity.Name}'.{note}", new JsonObject { ["nodeId"] = id }, warnings);
    }

    private async Task<OperationOutcome> SetInputAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var node = NodeOrRefuse(graph, Require(op, "node"), entity);
        var input = Require(op, "input");
        if (!op.Has("value"))
            throw Refuse("missing_argument", "'set_input' needs 'value'.", "Add \"value\" to the operation (use remove_input to clear one).");

        var details = DescriptorOrRefuse(snapshot, node["nodeType"]!.GetValue<string>(), suggest: false);
        var warnings = ApplyInput(graph, node, details, input, op.Args["value"], entity);
        return Ok(index, op, $"Set '{input}' on '{node["id"]!.GetValue<string>()}'.", warnings: warnings);
    }

    private async Task<OperationOutcome> RemoveInputAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var node = NodeOrRefuse(graph, Require(op, "node"), entity);
        var input = Require(op, "input");
        var removed = node["inputs"] is JsonObject inputs && inputs.Remove(input);
        return removed
            ? Ok(index, op, $"Cleared '{input}' on '{node["id"]!.GetValue<string>()}'.")
            : throw Refuse("unknown_input", $"'{input}' has no value on '{node["id"]!.GetValue<string>()}'.", options: [.. (node["inputs"] as JsonObject)?.Select(p => p.Key) ?? []]);
    }

    /// <summary>Checks a value against what the node declares for that input, then writes it. Returns warnings.</summary>
    private List<string> ApplyInput(JsonObject graph, JsonObject node, JsonObject details, string input, JsonNode? value, ChangesetEntity entity)
    {
        var warnings = new List<string>();
        var declared = (details["inputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var descriptor = declared.FirstOrDefault(d => string.Equals(d["name"]?.GetValue<string>(), input, StringComparison.OrdinalIgnoreCase));
        var exact = descriptor?["name"]?.GetValue<string>() ?? input;

        if (descriptor is null)
        {
            if (declared.Any(d => d["triggersReconfigure"]?.GetValue<bool>() == true))
                warnings.Add($"'{input}' is not in the catalogue of this node; accepted because this node's inputs depend on its configuration.");
            else
                throw Refuse("unknown_input", $"The node '{node["nodeType"]!.GetValue<string>()}' has no input '{input}'.", "Use one of the inputs the node declares.", [.. declared.Select(d => d["name"]!.GetValue<string>())]);
        }
        else
        {
            var coerced = Coerce(descriptor, exact, value, node["nodeType"]!.GetValue<string>());
            value = coerced;
        }

        var isTemplate = value is JsonValue tv && tv.TryGetValue<string>(out var text) && text.Contains("{{", StringComparison.Ordinal);
        if (isTemplate)
            warnings.AddRange(CheckReferences(graph, node, exact, value!.GetValue<string>()));

        (node["inputs"] as JsonObject ?? (JsonObject)(node["inputs"] = new JsonObject()))[exact] = value?.DeepClone();
        return warnings;
    }

    private static JsonNode? Coerce(JsonObject descriptor, string name, JsonNode? value, string nodeType)
    {
        var type = descriptor["type"]?.GetValue<string>() ?? "Text";
        if (value is null)
            return null;

        // A reference to another value is resolved when the workflow runs; its content cannot be judged now.
        if (value is JsonValue template && template.TryGetValue<string>(out var s) && s.Contains("{{", StringComparison.Ordinal))
            return value;

        return type switch
        {
            "Number" => CoerceNumber(value, name, nodeType),
            "Boolean" => CoerceBoolean(value, name, nodeType),
            "Choice" => CoerceChoice(descriptor, value, name, nodeType),
            "TextList" => value is JsonArray ? value : value is JsonValue ? new JsonArray(value.DeepClone()) : throw Invalid(name, nodeType, "a list of texts"),
            _ => value,
        };
    }

    private static JsonNode CoerceNumber(JsonNode value, string name, string nodeType)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
                return JsonValue.Create(d)!;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return JsonValue.Create(parsed)!;
        }

        throw Invalid(name, nodeType, "a number");
    }

    private static JsonNode CoerceBoolean(JsonNode value, string name, string nodeType)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b))
                return JsonValue.Create(b)!;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed))
                return JsonValue.Create(parsed)!;
        }

        throw Invalid(name, nodeType, "true or false");
    }

    private static JsonNode CoerceChoice(JsonObject descriptor, JsonNode value, string name, string nodeType)
    {
        var options = (descriptor["choices"] as JsonArray)?.OfType<JsonObject>().Select(c => c["value"]!.GetValue<string>()).ToList() ?? [];
        if (value is JsonValue v && v.TryGetValue<string>(out var s))
        {
            var match = options.FirstOrDefault(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return JsonValue.Create(match)!;
            if (options.Count == 0)
                return value;
        }

        throw Refuse("invalid_value", $"'{value}' is not a valid value for '{name}' on '{nodeType}'.", $"Choose one of: {string.Join(", ", options)}.", options);
    }

    private static Refusal Invalid(string name, string nodeType, string expected) => Refuse("invalid_value", $"'{name}' on '{nodeType}' expects {expected}.", $"Give {expected}, or a reference such as {{{{ node.output }}}}.");

    private IEnumerable<string> CheckReferences(JsonObject graph, JsonObject node, string input, string text)
    {
        foreach (Match match in NodeReference().Matches(text))
        {
            var from = match.Groups[1].Value;
            var field = match.Groups[2].Value.Split('.')[0].Split('[')[0];
            var target = FindNode(graph, from);
            if (target is null)
                continue; // may be a workflow variable or a state key: not judged

            var details = registry.Current.Get(CapabilityKinds.Node, target["nodeType"]!.GetValue<string>())?.Details;
            if (details is null || details["dynamicOutputs"] is not null)
                continue;
            var outputs = (details["outputs"] as JsonArray)?.OfType<JsonObject>().Select(o => o["name"]!.GetValue<string>()).ToList() ?? [];
            if (outputs.Count > 0 && !outputs.Contains(field, StringComparer.OrdinalIgnoreCase))
                yield return $"'{input}' on '{node["id"]!.GetValue<string>()}' refers to {from}.{field}, but '{from}' only gives: {string.Join(", ", outputs)}.";
        }
    }

    private async Task<OperationOutcome> ConnectAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var fromId = Require(op, "from");
        var toId = Require(op, "to");
        var from = NodeOrRefuse(graph, fromId, entity);
        var to = NodeOrRefuse(graph, toId, entity);
        if (string.Equals(from["id"]!.GetValue<string>(), to["id"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            throw Refuse("self_connection", "A node cannot be connected to itself.");

        var fromDetails = DescriptorOrRefuse(snapshot, from["nodeType"]!.GetValue<string>(), suggest: false);
        var toDetails = DescriptorOrRefuse(snapshot, to["nodeType"]!.GetValue<string>(), suggest: false);
        var outputs = Ports(fromDetails, "outputPorts");
        var inputs = Ports(toDetails, "inputPorts");

        // A step that declares no flow ports (the interaction steps) still has the platform's implicit "in" and "out".
        if (!outputs.Any(p => PortKind(p) != "resource") && fromDetails["nodeClass"]?.GetValue<string>() != "resource")
            outputs = [.. outputs, new JsonObject { ["name"] = "out", ["kind"] = "data" }];
        if (!inputs.Any(p => PortKind(p) != "resource") && toDetails["nodeClass"]?.GetValue<string>() != "resource")
            inputs = [.. inputs, new JsonObject { ["name"] = "in", ["kind"] = "data" }];

        var fromPort = op.Text("fromPort");
        var toPort = op.Text("toPort");
        var notes = new List<string>();

        // A resource (a model, a memory…) is connected from the resource to the one that uses it. Models often give it the
        // other way round: when only the reverse can fit, take it the reverse way instead of refusing.
        var providesResource = outputs.Any(p => PortKind(p) == "resource");
        var otherOutputs = Ports(toDetails, "outputPorts");
        var otherInputs = Ports(fromDetails, "inputPorts");
        if (!providesResource && otherOutputs.Any(p => PortKind(p) == "resource") && otherInputs.Any(p => PortKind(p) == "resource"))
        {
            (from, to) = (to, from);
            (fromDetails, toDetails) = (toDetails, fromDetails);
            (outputs, inputs) = (otherOutputs, otherInputs);
            (fromPort, toPort) = (toPort, fromPort);
            notes.Add($"I connected them the other way round: the resource goes from '{from["id"]}' to '{to["id"]}'.");
        }

        JsonObject? outPort = null;
        JsonObject? inPort = null;

        // A port name that does not exist is not fatal when the right one can be deduced below.
        if (fromPort is not null)
        {
            outPort = outputs.FirstOrDefault(p => string.Equals(p["name"]!.GetValue<string>(), fromPort, StringComparison.OrdinalIgnoreCase));
            if (outPort is null)
                notes.Add($"'{from["id"]}' has no output port '{fromPort}' (it has: {string.Join(", ", outputs.Select(p => p["name"]!.GetValue<string>()))}); I chose the one that fits.");
        }

        if (toPort is not null)
        {
            inPort = inputs.FirstOrDefault(p => string.Equals(p["name"]!.GetValue<string>(), toPort, StringComparison.OrdinalIgnoreCase));
            if (inPort is null)
                notes.Add($"'{to["id"]}' has no input port '{toPort}' (it has: {string.Join(", ", inputs.Select(p => p["name"]!.GetValue<string>()))}); I chose the one that fits.");
        }

        // The port that was not named is deduced when there is only one sensible choice.
        if (outPort is null)
        {
            var candidates = outputs.Where(p => inPort is null || PortKind(p) == PortKind(inPort)).ToList();
            outPort = Pick(candidates, "out", from["id"]!.GetValue<string>(), "output");
        }

        if (inPort is null)
        {
            var kind = PortKind(outPort);
            var candidates = inputs.Where(p => PortKind(p) == kind).ToList();
            if (kind == "resource")
            {
                var resourceType = outPort["resourceType"]?.GetValue<string>();
                var matching = candidates.Where(p => Compatible(resourceType, p["resourceType"]?.GetValue<string>())).ToList();
                if (matching.Count > 0)
                    candidates = matching;
            }

            inPort = Pick(candidates, "in", to["id"]!.GetValue<string>(), "input");
        }

        if (PortKind(outPort) != PortKind(inPort))
            throw Refuse("port_mismatch", $"'{outPort["name"]}' is a {PortKind(outPort)} port and '{inPort["name"]}' is a {PortKind(inPort)} port: they cannot be connected.");
        if (PortKind(outPort) == "resource" && !Compatible(outPort["resourceType"]?.GetValue<string>(), inPort["resourceType"]?.GetValue<string>()))
            throw Refuse("port_mismatch", $"'{from["id"]}' provides {outPort["resourceType"]} but '{inPort["name"]}' on '{to["id"]}' takes {inPort["resourceType"]}.");

        var fromName = outPort["name"]!.GetValue<string>();
        var toName = inPort["name"]!.GetValue<string>();
        var connections = Connections(graph);
        if (connections.OfType<JsonObject>().Any(c => Same(c, from["id"]!.GetValue<string>(), fromName, to["id"]!.GetValue<string>(), toName)))
            return Ok(index, op, $"'{from["id"]}' is already connected to '{to["id"]}'.");

        var max = inPort["maxConnections"]?.GetValue<int>() ?? -1;
        if (max > 0 && connections.OfType<JsonObject>().Count(c => string.Equals(c["to"]?.GetValue<string>(), to["id"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase) && string.Equals(c["toPort"]?.GetValue<string>(), toName, StringComparison.OrdinalIgnoreCase)) >= max)
            throw Refuse("port_full", $"'{toName}' on '{to["id"]}' accepts {max} connection(s) and is already taken.", "Disconnect the current one first, or use another node.");

        connections.Add(new JsonObject { ["from"] = from["id"]!.GetValue<string>(), ["fromPort"] = fromName, ["to"] = to["id"]!.GetValue<string>(), ["toPort"] = toName });
        return Ok(index, op, $"Connected '{from["id"]}' ({fromName}) to '{to["id"]}' ({toName}).", warnings: notes);
    }

    private static string PortKind(JsonObject port) => port["kind"]?.GetValue<string>() ?? "data";

    private static List<JsonObject> Ports(JsonObject details, string key) => (details[key] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    private static JsonObject Pick(List<JsonObject> candidates, string preferred, string nodeId, string direction)
    {
        if (candidates.Count == 1)
            return candidates[0];
        var named = candidates.FirstOrDefault(p => PortKind(p) == "data" && string.Equals(p["name"]!.GetValue<string>(), preferred, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
            return named;
        if (candidates.Count == 0)
            throw Refuse("no_port", $"'{nodeId}' has no {direction} port that fits this connection.", "Check the ports with describe_capability: a data connection goes from an output port to an input port, a resource (model, memory, tool) from the resource to the step that uses it.");
        throw Refuse("ambiguous_port", $"'{nodeId}' has several {direction} ports that could fit ({string.Join(", ", candidates.Select(p => p["name"]!.GetValue<string>()))}); say which one.", "Name it with fromPort / toPort.", [.. candidates.Select(p => p["name"]!.GetValue<string>())]);
    }

    /// <summary>Two resource types fit when one is the other, or when a wildcard on one covers the other.</summary>
    internal static bool Compatible(string? output, string? input)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(input))
            return true; // untyped: not judged
        var a = output.TrimEnd('*');
        var b = input.TrimEnd('*');
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(JsonObject c, string from, string fromPort, string to, string toPort) =>
        string.Equals(c["from"]?.GetValue<string>(), from, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c["to"]?.GetValue<string>(), to, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c["fromPort"]?.GetValue<string>() ?? "", fromPort, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c["toPort"]?.GetValue<string>() ?? "", toPort, StringComparison.OrdinalIgnoreCase);

    private async Task<OperationOutcome> DisconnectAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var from = Require(op, "from");
        var to = Require(op, "to");
        var fromPort = op.Text("fromPort");
        var toPort = op.Text("toPort");
        var connections = Connections(graph);
        var matches = connections
            .OfType<JsonObject>()
            .Where(c =>
                string.Equals(c["from"]?.GetValue<string>(), from, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c["to"]?.GetValue<string>(), to, StringComparison.OrdinalIgnoreCase)
                && (fromPort is null || string.Equals(c["fromPort"]?.GetValue<string>(), fromPort, StringComparison.OrdinalIgnoreCase))
                && (toPort is null || string.Equals(c["toPort"]?.GetValue<string>(), toPort, StringComparison.OrdinalIgnoreCase))
            )
            .ToList();
        if (matches.Count == 0)
            throw Refuse("unknown_connection", $"'{from}' is not connected to '{to}'.");
        foreach (var m in matches)
            connections.Remove(m);
        return Ok(index, op, $"Disconnected '{from}' from '{to}' ({matches.Count} connection(s)).");
    }

    private async Task<OperationOutcome> MoveNodeAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var node = NodeOrRefuse(graph, Require(op, "node"), entity);
        var x = op.Int("x") ?? throw Refuse("missing_argument", "'move_node' needs 'x' and 'y'.");
        var y = op.Int("y") ?? throw Refuse("missing_argument", "'move_node' needs 'x' and 'y'.");
        node["position"] = new JsonObject { ["x"] = x, ["y"] = y };
        return Ok(index, op, $"Moved '{node["id"]!.GetValue<string>()}'.");
    }

    private async Task<OperationOutcome> RemoveNodeAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var node = NodeOrRefuse(graph, Require(op, "node"), entity);
        var id = node["id"]!.GetValue<string>();
        Nodes(graph).Remove(node);
        var connections = Connections(graph);
        var dropped = connections.OfType<JsonObject>().Where(c => string.Equals(c["from"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase) || string.Equals(c["to"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in dropped)
            connections.Remove(c);

        var note = "";
        if (string.Equals(graph["entryNode"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
        {
            var next = Nodes(graph).OfType<JsonObject>().FirstOrDefault(n => snapshot.Get(CapabilityKinds.Node, n["nodeType"]!.GetValue<string>())?.Details["nodeClass"]?.GetValue<string>() != "resource");
            graph["entryNode"] = next?["id"]?.GetValue<string>() ?? "";
            note = next is null ? " The graph has no entry point now." : $" '{next["id"]}' is the entry point now.";
        }

        return Ok(index, op, $"Removed '{id}' and its {dropped.Count} connection(s).{note}");
    }

    private async Task<OperationOutcome> SetEntryAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var entity = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        var graph = GraphOf(entity, op.Op);
        var node = NodeOrRefuse(graph, Require(op, "node"), entity);
        graph["entryNode"] = node["id"]!.GetValue<string>();
        return Ok(index, op, $"'{node["id"]!.GetValue<string>()}' is the entry point of '{entity.Name}'.");
    }

    // ── Team ──────────────────────────────────────────────────────────────────────────────────

    private static void RequireTeam(ChangesetEntity entity, string op)
    {
        if (entity.Kind != EntityKinds.Team)
            throw Refuse("wrong_kind", $"'{op}' works on a team, not on the {entity.Kind} '{entity.Name}'.", "A workflow or an agent is built with add_node, set_input and connect.");
        if (entity.Draft["members"] is not JsonArray)
            entity.Draft["members"] = new JsonArray();
        if (entity.Draft["connections"] is not JsonArray)
            entity.Draft["connections"] = new JsonArray();
    }

    private static JsonArray Members(ChangesetEntity team) => (JsonArray)team.Draft["members"]!;

    private static JsonObject? FindMember(ChangesetEntity team, string position) => Members(team).OfType<JsonObject>().FirstOrDefault(m => string.Equals(m["topologyPositionId"]?.GetValue<string>(), position, StringComparison.OrdinalIgnoreCase));

    private static JsonObject MemberOrRefuse(ChangesetEntity team, string position) =>
        FindMember(team, position) ?? throw Refuse("unknown_member", $"There is no member '{position}' in '{team.Name}'.", options: [.. Members(team).OfType<JsonObject>().Select(m => m["topologyPositionId"]!.GetValue<string>())]);

    private async Task<OperationOutcome> SetOrchestratorAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var id = Require(op, "orchestrator");
        var known = snapshot.List(CapabilityKinds.Orchestrator);
        var match = known.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw Refuse("unknown_orchestrator", $"'{id}' is not an orchestration pattern of this platform.", "Choose the pattern that fits how the members should work together.", [.. known.Select(o => o.Id)]);
        team.Draft["orchestratorId"] = match.Id;
        return Ok(index, op, $"'{team.Name}' now works as '{match.Name}'.");
    }

    private async Task<OperationOutcome> AddMemberAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var memberRef = Require(op, "member");
        var member = await FindEntityAsync(memberRef, work, ct).ConfigureAwait(false);
        if (member.Kind == EntityKinds.Team && string.Equals(member.Id, team.Id, StringComparison.OrdinalIgnoreCase))
            throw Refuse("self_member", "A team cannot be its own member.");

        var memberType = member.Kind switch
        {
            EntityKinds.Agent => "Agent",
            EntityKinds.Team => "Team",
            _ => "Workflow",
        };

        string position;
        if (op.Text("position") is { } requested)
        {
            if (NotIdentifier().IsMatch(requested))
                throw Refuse("invalid_id", $"'{requested}' is not a valid position id.", "Use letters, digits, '-' and '_'.");
            if (FindMember(team, requested) is not null)
                throw Refuse("duplicate_id", $"'{requested}' is already a member of '{team.Name}'.");
            position = requested;
        }
        else
        {
            var stem = NotIdentifier().Replace(member.Name.ToLowerInvariant(), "_").Trim('_');
            if (stem.Length == 0)
                stem = "member";
            position = stem;
            var n = 2;
            while (FindMember(team, position) is not null)
                position = $"{stem}_{n++}";
        }

        var entry = new JsonObject { ["topologyPositionId"] = position, ["memberEntityId"] = member.Id, ["memberType"] = memberType, ["displayName"] = op.Text("display_name") ?? member.Name };
        if (op.Text("role") is { } role)
            entry["role"] = role;
        var group = op.Text("group") is { } named ? GroupName(named) : null;
        if (group is not null)
            entry["group"] = group;
        Members(team).Add(entry);
        // A member with no group is drawn apart from the themes of its team: said, so that it is given one.
        string[] warnings = group is null ? [$"'{member.Name}' is in no group. Give it the theme it belongs to with set_member_group (a short name shared by the members that deal with the same thing)."] : [];
        return Ok(index, op, $"Added '{member.Name}' to '{team.Name}' as '{position}'{(group is null ? "" : $", in the group '{group}'")}.", new JsonObject { ["position"] = position, ["memberEntityId"] = member.Id }, warnings);
    }

    /// <summary>The longest a group name can be: the platform refuses a longer one when the team is saved.</summary>
    private const int GroupMaxLength = 32;

    /// <summary>A group name as the platform keeps it: trimmed, inner spaces collapsed, and short enough.</summary>
    private static string GroupName(string given)
    {
        var name = string.Join(' ', given.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
            throw Refuse("invalid_group", "A group needs a name.", "Name the theme the member belongs to, in a word or two.");
        if (name.Length > GroupMaxLength)
            throw Refuse("invalid_group", $"The group name '{name}' has {name.Length} characters.", $"Keep it to {GroupMaxLength} or fewer: a theme in a word or two.");
        return name;
    }

    /// <summary>
    /// Puts a member in a group: the theme it belongs to, shared by the members that deal with the same thing. Members of a
    /// group are drawn together on the team's picture. It changes nothing in how the team runs.
    /// </summary>
    private async Task<OperationOutcome> SetMemberGroupAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var member = MemberOrRefuse(team, Require(op, "position"));
        var group = GroupName(Require(op, "group"));
        // One spelling per group: "booking" joins the "Booking" that is already there.
        var existing = Members(team).OfType<JsonObject>().Select(m => m["group"]?.GetValue<string>()).FirstOrDefault(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        member["group"] = existing ?? group;
        return Ok(index, op, $"'{member["displayName"]?.GetValue<string>() ?? member["topologyPositionId"]!.GetValue<string>()}' is in the group '{existing ?? group}'.");
    }

    private async Task<OperationOutcome> RemoveMemberAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var member = MemberOrRefuse(team, Require(op, "position"));
        var position = member["topologyPositionId"]!.GetValue<string>();
        Members(team).Remove(member);
        var connections = (JsonArray)team.Draft["connections"]!;
        foreach (var c in connections.OfType<JsonObject>().Where(c => string.Equals(c["fromTopologyPositionId"]?.GetValue<string>(), position, StringComparison.OrdinalIgnoreCase) || string.Equals(c["toTopologyPositionId"]?.GetValue<string>(), position, StringComparison.OrdinalIgnoreCase)).ToList())
            connections.Remove(c);
        return Ok(index, op, $"Removed '{position}' from '{team.Name}'.");
    }

    private async Task<OperationOutcome> SetManagerAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var member = MemberOrRefuse(team, Require(op, "position"));
        // The manager is the member at the top of the topology — the first one: its position makes it the manager.
        var members = Members(team);
        members.Remove(member);
        members.Insert(0, member);
        return Ok(index, op, $"'{member["displayName"] ?? member["topologyPositionId"]}' manages '{team.Name}'.");
    }

    private async Task<OperationOutcome> SetGoalAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        team.Draft["goal"] = Require(op, "goal");
        return Ok(index, op, $"Set the goal of '{team.Name}'.");
    }

    private async Task<OperationOutcome> ConnectMembersAsync(int index, BuilderOperation op, Work work, CapabilitySnapshot snapshot, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var from = MemberOrRefuse(team, Require(op, "from"))["topologyPositionId"]!.GetValue<string>();
        var to = MemberOrRefuse(team, Require(op, "to"))["topologyPositionId"]!.GetValue<string>();
        var type = Require(op, "type");

        var orchestratorId = team.Draft["orchestratorId"]?.GetValue<string>();
        var orchestrator = string.IsNullOrWhiteSpace(orchestratorId) ? null : snapshot.Get(CapabilityKinds.Orchestrator, orchestratorId);
        var allowed = (orchestrator?.Details["connectionRules"] as JsonArray)?.OfType<JsonObject>().Where(r => r["cardinality"]?.GetValue<string>() != "Forbidden").Select(r => r["type"]!.GetValue<string>()).ToList();
        string canonical = type;
        if (allowed is { Count: > 0 })
        {
            var match = allowed.FirstOrDefault(a => string.Equals(a, type, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw Refuse("invalid_connection_type", $"'{orchestrator!.Name}' does not accept '{type}' connections.", "Use a connection type the pattern accepts.", allowed);
            canonical = match;
        }

        var connections = (JsonArray)team.Draft["connections"]!;
        if (connections.OfType<JsonObject>().Any(c => string.Equals(c["fromTopologyPositionId"]?.GetValue<string>(), from, StringComparison.OrdinalIgnoreCase) && string.Equals(c["toTopologyPositionId"]?.GetValue<string>(), to, StringComparison.OrdinalIgnoreCase)))
            return Ok(index, op, $"'{from}' is already connected to '{to}'.");

        connections.Add(new JsonObject { ["fromTopologyPositionId"] = from, ["toTopologyPositionId"] = to, ["types"] = new JsonArray(canonical) });
        return Ok(index, op, $"Connected '{from}' to '{to}' ({canonical}).", warnings: orchestrator is null ? ["The team has no orchestration pattern yet; the connection type was not checked."] : null);
    }

    private async Task<OperationOutcome> DisconnectMembersAsync(int index, BuilderOperation op, Work work, CancellationToken ct)
    {
        var team = await ResolveAsync(op, work, ct).ConfigureAwait(false);
        RequireTeam(team, op.Op);
        var from = Require(op, "from");
        var to = Require(op, "to");
        var connections = (JsonArray)team.Draft["connections"]!;
        var matches = connections.OfType<JsonObject>().Where(c => string.Equals(c["fromTopologyPositionId"]?.GetValue<string>(), from, StringComparison.OrdinalIgnoreCase) && string.Equals(c["toTopologyPositionId"]?.GetValue<string>(), to, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            throw Refuse("unknown_connection", $"'{from}' is not connected to '{to}'.");
        foreach (var m in matches)
            connections.Remove(m);
        return Ok(index, op, $"Disconnected '{from}' from '{to}'.");
    }

    // ── Finding entities ──────────────────────────────────────────────────────────────────────

    /// <summary>The entity named by the <c>entity</c> argument, loaded into the changeset (as modified) when it is a saved one.</summary>
    private async Task<ChangesetEntity> ResolveAsync(BuilderOperation op, Work work, CancellationToken ct)
    {
        var reference = op.Text("entity") ?? throw Refuse("missing_argument", $"'{op.Op}' needs 'entity'.", "Give the id returned by create_entity, or the name of an existing entity.");
        var found = await FindEntityAsync(reference, work, ct).ConfigureAwait(false);
        return work.ById(found.Id) ?? throw Refuse("unknown_entity", $"No entity '{reference}'.");
    }

    private async Task<ChangesetEntity> FindEntityAsync(string reference, Work work, CancellationToken ct)
    {
        var inChangeset = work.ById(reference);
        if (inChangeset is not null)
        {
            if (inChangeset.State == EntityStates.Deleted)
                throw Refuse("deleted_entity", $"'{inChangeset.Name}' is deleted in this changeset.");
            return inChangeset;
        }

        var byName = work.Entities.Where(e => e.State != EntityStates.Deleted && string.Equals(e.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
            return byName[0];
        if (byName.Count > 1)
            throw Refuse("ambiguous_entity", $"Several entities in this changeset are named '{reference}'.", "Use the id.", [.. byName.Select(e => e.Id)]);

        var live = await lookup.GetAsync(reference, ct).ConfigureAwait(false);
        if (live is null)
        {
            var named = await lookup.FindByNameAsync(reference, ct).ConfigureAwait(false);
            if (named.Count == 1)
                live = named[0];
            else if (named.Count > 1)
                throw Refuse("ambiguous_entity", $"Several saved entities are named '{reference}'.", "Use the id.", [.. named.Select(n => $"{n.Kind}:{n.Id}")]);
        }

        if (live is null)
            throw Refuse("unknown_entity", $"There is no entity '{reference}' in this changeset or in the workspace.", "Create it with create_entity, or use an existing id or name. If you created it in a batch that was refused, nothing was created: send the create_entity again, in the same batch as what depends on it.");

        if (work.ById(live.Id) is { } already)
            return already;

        var entity = new ChangesetEntity
        {
            Id = live.Id,
            Kind = live.Kind,
            Name = live.Name,
            State = EntityStates.Modified,
            Draft = (JsonObject)live.Json.DeepClone(),
            BaseFingerprint = Fingerprint.Of(live.Json),
        };
        work.Entities.Add(entity);
        return entity;
    }
}
