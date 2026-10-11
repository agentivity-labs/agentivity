using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Capabilities;
using Agentivity.Module.Assistant.Changesets;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>One thing wrong in a spec, where it is, and what would make it right.</summary>
public sealed record SpecProblem(string Where, string Message, string? Hint = null, IReadOnlyList<string>? Options = null);

public sealed class SpecResult
{
    public bool Built { get; init; }

    public List<SpecProblem> Problems { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    /// <summary>The solution once built: the kind and the name of the main entity, the one that holds the rest.</summary>
    public JsonObject? Solution { get; init; }

    /// <summary>What was created: kind, name, id and how many steps.</summary>
    public List<JsonObject> Created { get; init; } = [];

    public IReadOnlyList<Validation.ValidationIssue> Issues { get; init; } = [];
}

/// <summary>
/// Builds whole entities from a light description: the steps (type and inputs), the resources and the connections, written in
/// the catalog's own identifiers. The description is turned into the checked operations the engine already applies, so the
/// full documents, ports, ids, positions and entry points come from the engine, never from the author.
/// All the problems of a description are reported at once. Nothing is applied unless the structure is sound.
/// </summary>
public sealed class SpecBuilder(IBuilderApi builder, OperationEngine engine, ICapabilityRegistry registry)
{
    /// <summary>One operation of the plan, with the names it creates and the ones it needs.</summary>
    private sealed record Step(BuilderOperation Op, string Where, string? Creates, IReadOnlyList<string> Needs, string Entity);

    public async Task<SpecResult?> BuildAsync(string changesetId, JsonNode? spec, CancellationToken cancellationToken = default)
    {
        var changeset = await builder.GetChangesetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        if (changeset is null || changeset.Status != "open")
            return null;

        var problems = new List<SpecProblem>();
        var notes = new List<string>();
        var plan = Translate(spec, registry.Current, changeset, problems, notes);
        if (problems.Count > 0)
            return new SpecResult { Built = false, Problems = problems };

        // Rehearsal on a copy: every step is tried, so that every problem is reported, not only the first.
        var copy = ChangesetJson.Read(ChangesetJson.Write(changeset));
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plan)
        {
            if (step.Needs.FirstOrDefault(n => failed.Contains(n)) is { } missing)
            {
                if (step.Creates is not null)
                    failed.Add(step.Creates);
                continue; // its cause is already reported
            }

            var result = await engine.ApplyAsync(copy, [step.Op], atomic: false, label: null, cancellationToken).ConfigureAwait(false);
            var outcome = result.Results.FirstOrDefault();
            if (outcome is null || !outcome.Ok)
            {
                problems.Add(new SpecProblem(step.Where, outcome?.Message ?? "Not applied.", outcome?.Error?.Hint, outcome?.Error?.Options));
                if (step.Creates is not null)
                    failed.Add(step.Creates);
                continue;
            }

            foreach (var warning in outcome.Warnings)
                notes.Add($"{step.Where}: {warning}");
        }

        if (problems.Count > 0)
            return new SpecResult { Built = false, Problems = problems, Notes = notes };

        var applied = await builder.ApplyOperationsAsync(changesetId, [.. plan.Select(p => p.Op)], atomic: true, label: "Built from a description", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (applied is null || !applied.Applied)
        {
            var refused = applied?.Results.FirstOrDefault(r => !r.Ok);
            return new SpecResult { Built = false, Problems = [new SpecProblem("build", refused?.Message ?? "The changeset is not open any more.", refused?.Error?.Hint)], Notes = notes };
        }

        var issues = await builder.ValidateAsync(changesetId, cancellationToken).ConfigureAwait(false) ?? [];
        var current = await builder.GetChangesetAsync(changesetId, cancellationToken).ConfigureAwait(false);
        var created = new List<JsonObject>();
        foreach (var name in plan.Where(p => p.Op.Op == "create_entity").Select(p => p.Entity).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entity = current?.Entities.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entity is not null)
                created.Add(new JsonObject { ["kind"] = entity.Kind, ["name"] = entity.Name, ["id"] = entity.Id, ["steps"] = plan.Count(p => p.Op.Op is "add_node" && string.Equals(p.Entity, name, StringComparison.OrdinalIgnoreCase)) });
        }

        var main = current?.RootEntityId is { } mainId ? current.Entities.FirstOrDefault(e => e.Id == mainId) : null;
        var solution = main is null ? null : new JsonObject { ["kind"] = main.Kind, ["name"] = main.Name, ["id"] = main.Id };
        return new SpecResult { Built = true, Notes = notes, Created = created, Issues = issues, Solution = solution };
    }

    // ── From the description to operations ────────────────────────────────────────────────────

    private static List<Step> Translate(JsonNode? spec, CapabilitySnapshot platform, Changeset changeset, List<SpecProblem> problems, List<string> notes)
    {
        var plan = new List<Step>();
        if (spec is not JsonObject root)
        {
            problems.Add(new SpecProblem("spec", "The description must be a JSON object.", "Send {\"workflow\": name, \"steps\": [...], \"connections\": [...]}, or {\"entities\": [ ... ]}."));
            return plan;
        }

        var entities = Gather(root);
        if (entities.Count == 0)
            problems.Add(new SpecProblem("spec", "There is nothing to build.", "Give a workflow, an agent or a team."));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var described = new List<(string Kind, string Name)>();
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            var at = entities.Count > 1 ? $"entities[{i}]" : "spec";
            var kind = Kinds.FirstOrDefault(k => entity[k] is JsonValue);
            var name = kind is null ? null : (entity[kind] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : null;
            if (kind is null || string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SpecProblem(at, "Each thing to build names its kind and its name: \"workflow\": \"Name\", \"agent\": \"Name\" or \"team\": \"Name\"."));
                continue;
            }

            if (!seen.Add(name))
                problems.Add(new SpecProblem(at, $"'{name}' appears twice in this description."));
            if (changeset.Entities.Any(e => e.State != EntityStates.Deleted && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                problems.Add(new SpecProblem(at, $"'{name}' already exists in this piece of work.", "Change it with apply_operations (set_input, add_node, connect…), or give this one another name."));

            described.Add((kind, name));
            if (kind == "team")
                TranslateTeam(entity, name, at, plan, problems);
            else
                TranslateGraph(entity, kind, name, at, platform, plan, problems, notes);
        }

        NameTheSolution(root, described, changeset, plan, problems);
        return plan;
    }

    /// <summary>
    /// The author says which of the things it describes is the solution: the one the person runs, which holds the others (it
    /// is the root of the piece of work, and gets the "solution" tag when saved). It is checked here against what the
    /// description really builds: "the solution is the team Trips" with four agents and no team is refused, with what is
    /// missing. Nothing is guessed from the order things were written in.
    /// </summary>
    private static void NameTheSolution(JsonObject root, List<(string Kind, string Name)> described, Changeset changeset, List<Step> plan, List<SpecProblem> problems)
    {
        var listed = described.Count == 0 ? "nothing" : string.Join(", ", described.Select(d => $"{d.Kind} '{d.Name}'"));
        var existing = changeset.Entities.Where(e => e.State != EntityStates.Deleted).ToList();

        string? kind = null;
        string? name;
        if (root["solution"] is JsonObject said)
        {
            kind = Text(said, "kind")?.ToLowerInvariant();
            name = Text(said, "name");
        }
        else
        {
            name = Text(root, "solution");
        }

        // A description sent in several parts: the solution may come in a later part. What is here is built; the solution is
        // set as soon as it exists, and the work is checked as a whole before the assistant says it is done.
        var partial = root["partial"] is JsonValue p && p.TryGetValue<bool>(out var more) && more;

        if (name is null)
        {
            if (partial)
                return;

            // One thing in an empty piece of work is the solution; a change to work that has its solution keeps it.
            if (root["solution"] is null && described.Count <= 1 && existing.Count == 0)
            {
                if (described.Count == 1)
                    plan.Add(new Step(new BuilderOperation("set_root", new JsonObject { ["entity"] = described[0].Name }), "solution", null, [], described[0].Name));
                return;
            }

            if (root["solution"] is null && changeset.RootEntityId is not null)
                return;

            problems.Add(new SpecProblem("solution", $"Say which one is the solution. This description builds: {listed}.", "Add \"solution\": {\"kind\": \"team\" | \"workflow\" | \"agent\", \"name\": its name}: the one the person will run, which contains the others. A team of agents is a team, described with its \"orchestrator\" and its \"members\"."));
            return;
        }

        if (kind is not null && !Kinds.Contains(kind))
        {
            problems.Add(new SpecProblem("solution", $"'{kind}' is not a kind of solution.", "It is \"team\", \"workflow\" or \"agent\".", Kinds));
            return;
        }

        (string Kind, string Name)? match = null;
        foreach (var d in described)
        {
            if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                match ??= d;
        }

        foreach (var e in existing)
        {
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                match ??= (e.Kind, e.Name);
        }

        if (match is null)
        {
            if (partial)
                return;

            var what = kind ?? "thing";
            var hint = kind == "team"
                ? "Describe the team itself among the entities: \"team\": \"" + name + "\", \"orchestrator\": one of the listed ways of working, \"members\": the names of its agents."
                : "Describe '" + name + "' itself, or name as the solution one of the things described.";
            problems.Add(new SpecProblem("solution", $"The solution is said to be the {what} '{name}', but nothing of that name is described. This description builds: {listed}.", hint));
            return;
        }

        if (kind is not null && match.Value.Kind != kind)
        {
            problems.Add(new SpecProblem("solution", $"The solution is said to be a {kind}, but '{match.Value.Name}' is described as {(match.Value.Kind == "agent" ? "an" : "a")} {match.Value.Kind}.", kind == "team" ? "A team is described with \"team\": name, its \"orchestrator\" and its \"members\"; its agents are described beside it." : null));
            return;
        }

        plan.Add(new Step(new BuilderOperation("set_root", new JsonObject { ["entity"] = match.Value.Name }), "solution", null, [], match.Value.Name));
    }

    private static readonly string[] Kinds = ["workflow", "agent", "team"];

    /// <summary>
    /// Everything a description asks to build, wherever it was written: the object itself, a list under "entities" (or
    /// "agents", "workflows", "teams"), a team given beside the list of its agents, an agent described inside the member that
    /// uses it. Nothing that names something to build is left out without a word: a team dropped silently is four agents and
    /// an assistant sure it built a team. Teams come last, so that the members they name exist when they are added.
    /// </summary>
    private static List<JsonObject> Gather(JsonObject root)
    {
        var found = new List<JsonObject>();

        void Take(JsonObject entity, string? kindOfList)
        {
            // {"team": {"name": ..., "members": [...]}}: the kind holds the description instead of the name.
            foreach (var k in Kinds)
            {
                if (entity[k] is JsonObject inner)
                {
                    var copy = (JsonObject)inner.DeepClone();
                    if (!Kinds.Any(copy.ContainsKey) && Text(copy, "name") is { } innerName)
                        copy[k] = innerName;
                    Take(copy, k);
                    return;
                }
            }

            // An item of "agents": [...] says its kind by the list it is in.
            if (kindOfList is not null && !Kinds.Any(k => entity[k] is JsonValue) && Text(entity, "name") is { } listed)
            {
                entity = (JsonObject)entity.DeepClone();
                entity[kindOfList] = listed;
            }

            // An agent described where the team uses it is built too, and the member keeps naming it.
            if (entity["team"] is JsonValue && entity["members"] is JsonArray members)
            {
                foreach (var member in members.OfType<JsonObject>().Where(m => m["steps"] is JsonArray || m["resources"] is JsonArray))
                {
                    var agentName = Text(member, "agent") ?? Text(member, "name");
                    if (agentName is null)
                        continue;
                    var agent = (JsonObject)member.DeepClone();
                    agent.Remove("id");
                    agent.Remove("role");
                    agent.Remove("position");
                    agent.Remove("name");
                    agent["agent"] = agentName;
                    found.Add(agent);
                }
            }

            found.Add(entity);
        }

        var lists = new (string Key, string? Kind)[] { ("entities", null), ("agents", "agent"), ("workflows", "workflow"), ("teams", "team") };
        var nested = false;
        foreach (var (key, kind) in lists)
        {
            if (root[key] is not JsonArray list)
                continue;
            nested = true;
            foreach (var item in list.OfType<JsonObject>())
                Take(item, kind);
        }

        // The object itself names something to build, alone or beside a list (a team written around its "entities").
        if (!nested || Kinds.Any(k => root[k] is JsonValue or JsonObject))
            Take(root, null);

        return [.. found.Where(e => e["team"] is not JsonValue), .. found.Where(e => e["team"] is JsonValue)];
    }

    private static void TranslateGraph(JsonObject entity, string kind, string name, string at, CapabilitySnapshot platform, List<Step> plan, List<SpecProblem> problems, List<string> notes)
    {
        plan.Add(new Step(new BuilderOperation("create_entity", new JsonObject { ["kind"] = kind, ["name"] = name }), at, null, [], name));

        var steps = entity["steps"] as JsonArray ?? [];
        var resources = entity["resources"] as JsonArray ?? [];
        if (steps.Count == 0 && resources.Count == 0)
            problems.Add(new SpecProblem(at, $"'{name}' has no steps.", "List its steps in \"steps\": [{\"id\", \"type\", \"inputs\"}]."));

        // A resource written among the steps is still a resource: the catalog says which a type is.
        var flow = new List<JsonObject>();
        var provided = new List<JsonObject>();
        foreach (var item in steps.OfType<JsonObject>())
        {
            var type = Text(item, "type");
            var isResource = type is not null && platform.Get(CapabilityKinds.Node, type)?.Details["nodeClass"]?.GetValue<string>() == "resource";
            (isResource ? provided : flow).Add(item);
        }

        provided.AddRange(resources.OfType<JsonObject>());

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in flow.Select((s, index) => (s, index)))
        {
            var id = Text(step.s, "id");
            var type = Text(step.s, "type");
            var where = $"steps[{step.index}]" + (id is null ? "" : $" '{id}'");
            if (id is null || type is null)
            {
                problems.Add(new SpecProblem($"{at}.{where}", "A step needs an \"id\" and a \"type\".", "Use ids you choose (letters, digits, - and _) and a type from the catalog."));
                continue;
            }

            if (!ids.Add(id))
            {
                problems.Add(new SpecProblem($"{at}.{where}", $"The id '{id}' is used twice."));
                continue;
            }

            var args = new JsonObject { ["entity"] = name, ["type"] = type, ["id"] = id };
            if (step.s["inputs"] is JsonObject inputs)
                args["inputs"] = inputs.DeepClone();
            plan.Add(new Step(new BuilderOperation("add_node", args), $"{at}.{where}", id, [], name));
        }

        // Connections: each resource is attached to the step that uses it; the others are the flow between steps.
        var connections = entity["connections"] as JsonArray ?? [];
        var users = new Dictionary<string, (string For, string? Port)>(StringComparer.OrdinalIgnoreCase);
        var flowLinks = new List<(string From, string To, string? FromPort, string? ToPort, string Where)>();
        for (var c = 0; c < connections.Count; c++)
        {
            var where = $"{at}.connections[{c}]";
            switch (connections[c])
            {
                case JsonArray pair when pair.Count >= 2 && pair[0] is JsonValue && pair[1] is JsonValue:
                    flowLinks.Add((Str(pair[0]), Str(pair[1]), pair.Count > 2 ? Str(pair[2]) : null, pair.Count > 3 ? Str(pair[3]) : null, where));
                    break;
                case JsonObject link when link.ContainsKey("resource"):
                    var res = Text(link, "resource");
                    var consumer = Text(link, "for") ?? Text(link, "to");
                    if (res is null || consumer is null)
                        problems.Add(new SpecProblem(where, "A resource connection says which resource and for which step: {\"resource\": id, \"for\": step}."));
                    else
                        users[res] = (consumer, Text(link, "port"));
                    break;
                case JsonObject link when Text(link, "from") is { } from && Text(link, "to") is { } to:
                    flowLinks.Add((from, to, Text(link, "fromPort"), Text(link, "toPort"), where));
                    break;
                default:
                    problems.Add(new SpecProblem(where, "A connection is [from, to] between two steps, or {\"resource\": id, \"for\": step}."));
                    break;
            }
        }

        foreach (var res in provided.Select((r, index) => (r, index)))
        {
            var id = Text(res.r, "id");
            var type = Text(res.r, "type");
            var where = $"{at}.resources[{res.index}]" + (id is null ? "" : $" '{id}'");
            if (id is null || type is null)
            {
                problems.Add(new SpecProblem(where, "A resource needs an \"id\" and a \"type\"."));
                continue;
            }

            // A resource is not referred to in the flow: its id only has to be unique, so a clash is settled here.
            if (!ids.Add(id))
            {
                var unique = id;
                for (var n = 2; !ids.Add(unique = $"{id}_{n}"); n++)
                {
                }

                notes.Add($"{where}: the id '{id}' was already used; this resource is called '{unique}'.");
                if (users.TryGetValue(id, out var shared))
                    users[unique] = shared;
                id = unique;
            }

            // A resource may also name its user itself.
            (string? For, string? Port) target = (null, null);
            if (users.TryGetValue(id, out var user))
                target = (user.For, user.Port);
            else if ((Text(res.r, "for") ?? Text(res.r, "to")) is { } own)
                target = (own, Text(res.r, "port"));
            if (target.For is null)
            {
                problems.Add(new SpecProblem(where, $"The resource '{id}' is not connected to a step.", $"Add {{\"resource\": \"{id}\", \"for\": \"<the step that uses it>\"}} to \"connections\"."));
                continue;
            }

            var args = new JsonObject { ["entity"] = name, ["type"] = type, ["id"] = id, ["to"] = target.For };
            if (target.Port is not null)
                args["port"] = target.Port;
            if (res.r["inputs"] is JsonObject inputs)
                args["inputs"] = inputs.DeepClone();
            plan.Add(new Step(new BuilderOperation("add_resource", args), where, id, [target.For], name));
        }

        foreach (var link in flowLinks)
        {
            var args = new JsonObject { ["entity"] = name, ["from"] = link.From, ["to"] = link.To };
            if (link.FromPort is not null)
                args["fromPort"] = link.FromPort;
            if (link.ToPort is not null)
                args["toPort"] = link.ToPort;
            plan.Add(new Step(new BuilderOperation("connect", args), link.Where, null, [link.From, link.To], name));
        }

        if (Text(entity, "entry") is { } entry)
            plan.Add(new Step(new BuilderOperation("set_entry", new JsonObject { ["entity"] = name, ["node"] = entry }), $"{at}.entry", null, [entry], name));

        // What a step requires and the description left out (its model, its session…) is filled in where there is no doubt.
        plan.Add(new Step(new BuilderOperation("complete_resources", new JsonObject { ["entity"] = name }), $"{at}.resources", null, [], name));
    }

    private static void TranslateTeam(JsonObject entity, string name, string at, List<Step> plan, List<SpecProblem> problems)
    {
        plan.Add(new Step(new BuilderOperation("create_entity", new JsonObject { ["kind"] = "team", ["name"] = name }), at, null, [], name));

        if ((Text(entity, "orchestrator") ?? Text(entity, "pattern")) is { } orchestrator)
            plan.Add(new Step(new BuilderOperation("set_orchestrator", new JsonObject { ["entity"] = name, ["orchestrator"] = orchestrator }), $"{at}.orchestrator", null, [], name));
        else
            problems.Add(new SpecProblem(at, $"The team '{name}' needs its way of working.", "Give \"orchestrator\": one of the ids listed in the instructions."));

        var members = entity["members"] as JsonArray ?? [];
        if (members.Count == 0)
            problems.Add(new SpecProblem(at, $"The team '{name}' has no members.", "List them in \"members\": [{\"id\", \"agent\"}]."));

        // How a member can be referred to later (manager, connections): its position id, its agent's name or its display name.
        var positionOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var placed = new List<(string Position, string Who)>();
        for (var m = 0; m < members.Count; m++)
        {
            var where = $"{at}.members[{m}]";
            // A member is the name of an agent (or team, or workflow), or an object naming it: agent, member, name, ref, entity.
            JsonObject member;
            if (members[m] is JsonValue plain && plain.TryGetValue<string>(out var plainName) && !string.IsNullOrWhiteSpace(plainName))
                member = new JsonObject { ["agent"] = plainName.Trim() };
            else if (members[m] is JsonObject given)
                member = given;
            else
            {
                problems.Add(new SpecProblem(where, $"A member names the agent (or team, or workflow) it is: \"Billing\" or {{\"id\": position, \"agent\": name}}. Received: {Shown(members[m])}."));
                continue;
            }

            var who = new[] { "agent", "member", "ref", "entity", "agentId", "agent_id", "memberId", "member_id", "name", "id" }.Select(k => Text(member, k)).FirstOrDefault(v => v is not null);
            if (who is null)
            {
                problems.Add(new SpecProblem(where, $"A member names the agent (or team, or workflow) it is: \"Billing\" or {{\"id\": position, \"agent\": name}}. Received: {Shown(member)}."));
                continue;
            }

            var args = new JsonObject { ["entity"] = name, ["member"] = who };
            var position = Text(member, "id") ?? Text(member, "position") ?? Slug(who);
            args["position"] = position;
            positionOf[position] = position;
            positionOf.TryAdd(who, position);
            if (Text(member, "name") is { } display)
            {
                args["display_name"] = display;
                positionOf.TryAdd(display, position);
            }
            if (Text(member, "role") is { } role)
                args["role"] = role;
            plan.Add(new Step(new BuilderOperation("add_member", args), where, null, [], name));
            if (Text(member, "group") is { } ownGroup)
                groupOf[position] = ownGroup;
            placed.Add((position, who));
        }

        // Groups written on the team, by theme: {"Booking": ["Flights", "Hotels"], "Payment": ["Cashier"]}.
        if (entity["groups"] is JsonObject groups)
        {
            foreach (var (groupName, listed) in groups)
            {
                foreach (var one in listed as JsonArray ?? [])
                {
                    var reference = Str(one);
                    if (positionOf.TryGetValue(reference, out var at2))
                        groupOf[at2] = groupName;
                    else
                        problems.Add(new SpecProblem($"{at}.groups", $"The group '{groupName}' lists '{reference}', which is not a member of '{name}'.", "List members by the name of their agent."));
                }
            }
        }

        // Every member belongs to a theme, a member alone in its theme too: it is what the picture of the team is made of.
        var ungrouped = placed.Where(p => !groupOf.ContainsKey(p.Position)).Select(p => p.Who).ToList();
        if (ungrouped.Count > 0)
            problems.Add(new SpecProblem($"{at}.groups", $"Put every member of '{name}' in a group. Without one: {string.Join(", ", ungrouped)}.", "Group the members by theme, what they deal with, in a word or two: \"groups\": {\"Booking\": [\"Flights\", \"Hotels\"], \"Coordination\": [\"Coordinator\"]}. Members that deal with the same thing share a group; one alone in its theme has its own."));
        foreach (var (position, groupName) in groupOf)
            plan.Add(new Step(new BuilderOperation("set_member_group", new JsonObject { ["entity"] = name, ["position"] = position, ["group"] = groupName }), $"{at}.groups", null, [], name));

        if (Text(entity, "manager") is { } manager)
            plan.Add(new Step(new BuilderOperation("set_manager", new JsonObject { ["entity"] = name, ["position"] = positionOf.GetValueOrDefault(manager, manager) }), $"{at}.manager", null, [], name));
        if (Text(entity, "goal") is { } goal)
            plan.Add(new Step(new BuilderOperation("set_goal", new JsonObject { ["entity"] = name, ["goal"] = goal }), $"{at}.goal", null, [], name));

        var connections = entity["connections"] as JsonArray ?? [];
        for (var c = 0; c < connections.Count; c++)
        {
            var where = $"{at}.connections[{c}]";
            if (connections[c] is JsonArray pair && pair.Count >= 3)
                plan.Add(new Step(new BuilderOperation("connect_members", new JsonObject { ["entity"] = name, ["from"] = positionOf.GetValueOrDefault(Str(pair[0]), Str(pair[0])), ["to"] = positionOf.GetValueOrDefault(Str(pair[1]), Str(pair[1])), ["type"] = Str(pair[2]) }), where, null, [], name));
            else
                problems.Add(new SpecProblem(where, "A connection between members is [from, to, type]."));
        }
    }

    /// <summary>A position id from a name: letters and digits, the rest becoming an underscore.</summary>
    private static string Slug(string text)
    {
        var slug = new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return slug.Length == 0 ? "member" : slug;
    }

    /// <summary>What was received, short enough to read in an error.</summary>
    private static string Shown(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "nothing";
        return text.Length <= 160 ? text : text[..157] + "...";
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : n?.ToString() ?? "";
}
