using System.Text.Json.Nodes;

namespace Agentivity.Module.Assistant.Changesets;

/// <summary>The kinds of entity a changeset can carry.</summary>
public static class EntityKinds
{
    public const string Workflow = "workflow";
    public const string Agent = "agent";
    public const string Team = "team";

    public static bool IsKnown(string? kind) => kind is Workflow or Agent or Team;
}

/// <summary>What happened to an entity inside a changeset, relative to the saved workspace.</summary>
public static class EntityStates
{
    public const string Created = "created";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
}

/// <summary>
/// A working copy of everything one piece of work touches. Entities live here as complete drafts; the saved workspace is
/// never changed until the changeset is applied, and applying is the person's act, not the assistant's.
/// </summary>
public sealed class Changeset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = "";

    /// <summary><c>open</c>, <c>saved</c> or <c>discarded</c>.</summary>
    public string Status { get; set; } = "open";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The person whose draft it is, with its conversation: no one else sees it. Null for one made before the assistant knew who
    /// spoke to it, which the first person who keeps it makes theirs.
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>The main entity of the solution, once there is one. It receives the <c>solution</c> tag when saved.</summary>
    public string? RootEntityId { get; set; }

    /// <summary>The folder of the agentic library new agents and teams are saved into, when the person chose one.</summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// The folder new workflows are saved into, when the person chose one. Workflows have their own folder tree, apart from
    /// the one agents and teams share: a folder of one does not exist in the other.
    /// </summary>
    public string? WorkflowFolderId { get; set; }

    /// <summary>
    /// The one place the person chose for what is new, as the names of the folders from the top level down (empty for the
    /// top level). Each library keeps its own folders, so the same path is followed in every library the save adds to,
    /// and the folders it lacks are created: the solution sits under the same name everywhere. When set, it is used
    /// instead of the two folder ids above.
    /// </summary>
    public List<string>? DestinationPath { get; set; }

    /// <summary>
    /// The model connection the agents and teams built here use. It is the person's choice and is independent of the model the
    /// assistant itself talks to: one provider can build, another can run what is built.
    /// </summary>
    public AgentModelChoice? AgentModel { get; set; }

    /// <summary>The capability registry version the changeset was last worked against.</summary>
    public string? RegistryVersion { get; set; }

    /// <summary>Counts every applied batch of operations; a revision can be returned to.</summary>
    public int Revision { get; set; }

    public List<ChangesetEntity> Entities { get; set; } = [];

    /// <summary>What was applied, in order, in plain words.</summary>
    public List<JournalEntry> Journal { get; set; } = [];

    /// <summary>The entities as they were before each of the last revisions.</summary>
    public List<RevisionSnapshot> Revisions { get; set; } = [];

    /// <summary>The conversation about this piece of work: what the person said and what the assistant answered.</summary>
    public List<ConversationMessage> Conversation { get; set; } = [];

    /// <summary>What the assistant understood and intends to build, as it last stated it.</summary>
    public AssistantPlan? Plan { get; set; }

    public ChangesetEntity? Find(string id) => Entities.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A deep copy of the entities, to work on without touching the changeset until the work is accepted.</summary>
    public List<ChangesetEntity> CloneEntities() => [.. Entities.Select(e => e.Clone())];
}

/// <summary>One entity inside a changeset: a complete draft plus how it relates to the saved one.</summary>
public sealed class ChangesetEntity
{
    public string Id { get; set; } = "";

    /// <summary>See <see cref="EntityKinds"/>.</summary>
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>See <see cref="EntityStates"/>.</summary>
    public string State { get; set; } = EntityStates.Created;

    /// <summary>The whole entity as JSON, in the same shape the API stores. Empty for a deleted entity.</summary>
    public JsonObject Draft { get; set; } = new();

    /// <summary>For a modified or deleted entity: a fingerprint of the saved version this draft started from.</summary>
    public string? BaseFingerprint { get; set; }

    public ChangesetEntity Clone() =>
        new()
        {
            Id = Id,
            Kind = Kind,
            Name = Name,
            State = State,
            BaseFingerprint = BaseFingerprint,
            Draft = (JsonObject)Draft.DeepClone(),
        };
}

/// <summary>One message of the conversation, kept in plain words. The tool exchanges of a turn are not kept, only a short account of the work.</summary>
public sealed class ConversationMessage
{
    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public string Role { get; set; } = "user";

    public string Content { get; set; } = "";

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>For an assistant message: what it did before answering, one plain line each.</summary>
    public List<string> Work { get; set; } = [];

    /// <summary>For an assistant message that ends on a question: the choices offered, if any.</summary>
    public List<string> Options { get; set; } = [];

    /// <summary>For a question whose answer is one of the person's connections: what to choose among.</summary>
    public ConnectionChoice? Pick { get; set; }

    /// <summary>
    /// For an assistant message: what the assistant is given back of this turn when the conversation goes on, in place of
    /// <see cref="Content"/>. Content is what the person reads last (often only a question); the assistant needs the whole of
    /// it to know where it stands: what it said, the plan it stated, what it did, what it asked. Null for older messages.
    /// </summary>
    public string? Memory { get; set; }
}

/// <summary>
/// A choice among the connections the person has, filled in by the platform. <see cref="Purpose"/> says what the answer is
/// for: <c>agent_model</c> (the model every agent of this piece of work runs on) or <c>connection</c> (one for a step).
/// </summary>
public sealed class ConnectionChoice
{
    /// <summary>What was asked for: <c>model</c>, or a kind of connection (smtp…).</summary>
    public string Kind { get; set; } = "";

    public string Purpose { get; set; } = "connection";

    public List<ConnectionOption> Connections { get; set; } = [];

    /// <summary>
    /// The kinds of connection that would answer the question and that the person can create on the spot (now, or as a
    /// placeholder to set up later), when they have none or want another.
    /// </summary>
    public List<ConnectionKindOption> Creatable { get; set; } = [];
}

/// <summary>A kind of connection the person can create: its id (the template) and its name.</summary>
public sealed class ConnectionKindOption
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
}

/// <summary>One connection offered: never its secrets.</summary>
public sealed class ConnectionOption
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? Description { get; set; }
}

/// <summary>The functional plan: what the person asked for, what will be built, what is needed from them.</summary>
/// <summary>A model connection chosen for the agents of a changeset. Kind is the provider (anthropic, openai...).</summary>
public sealed class AgentModelChoice
{
    public string ConnectionId { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";
}

public sealed class AssistantPlan
{
    /// <summary>The request restated as a goal.</summary>
    public string Brief { get; set; } = "";

    /// <summary>What will be created or changed, in business words.</summary>
    public List<string> Steps { get; set; } = [];

    /// <summary>What only the person can provide (an account, a key, a decision).</summary>
    public List<string> Needs { get; set; } = [];

    /// <summary>A name proposed for the solution.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// What will exist once the plan is carried out, each with its kind. It is what the work is checked against before the
    /// assistant says it is done: a plan for a team is not done with four agents and no team.
    /// </summary>
    public List<PlannedEntity> Builds { get; set; } = [];

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The answers the person was offered with the plan. The first is the go-ahead; picking another one is asking for a
    /// change, which does not confirm the plan.
    /// </summary>
    public List<string> Options { get; set; } = [];

    /// <summary>
    /// True when the person agreed by picking the go-ahead itself (the first answer offered), not by writing something of
    /// their own that may hold a request. A plan agreed to that way is not put to them again before it is built.
    /// </summary>
    public bool GoAhead { get; set; }

    /// <summary>
    /// True once the person has answered after the plan was stated. Nothing new is built before: the plan is read first. A
    /// revised plan starts unconfirmed again.
    /// </summary>
    public bool Confirmed { get; set; }
}

/// <summary>One thing a plan says will be built: <c>workflow</c>, <c>agent</c> or <c>team</c>, and its name.</summary>
public sealed class PlannedEntity
{
    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>For an agent: each tool it gets and why, in a few words ("Date & Time: to count the 30 days of the returns policy").</summary>
    public List<string> Tools { get; set; } = [];
}

public sealed class JournalEntry
{
    public int Revision { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string Op { get; set; } = "";

    public string Message { get; set; } = "";
}

public sealed class RevisionSnapshot
{
    public int Revision { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string Label { get; set; } = "";

    public List<ChangesetEntity> Entities { get; set; } = [];

    public string? RootEntityId { get; set; }
}
