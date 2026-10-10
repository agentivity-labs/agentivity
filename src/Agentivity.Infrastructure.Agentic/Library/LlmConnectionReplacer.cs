using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;
using CredentialModel = Agentivity.Domain.Credentials.Credentials;

namespace Agentivity.Infrastructure.Agentic.Library;

public enum LibraryEntityKind
{
    Agent,
    Team,
    Workflow,
}

public sealed record LlmReplacementRoot(LibraryEntityKind Kind, string Id);

/// <param name="Status">updated | unchanged | no_llm | outside_scope | failed</param>
/// <param name="Depth">0 for the item asked for, 1 for what it contains, and so on.</param>
public sealed record LlmReplacementItem(LibraryEntityKind Kind, string Id, string Name, int Depth, int LlmNodes, int Replaced, bool ProviderChanged, string Status, string? Note = null);

/// <summary>A reference that could not be followed: the thing it points to is missing, or only known at run time.</summary>
public sealed record LlmReplacementUnresolved(LibraryEntityKind FromKind, string FromId, string FromName, string Reference, string Reason);

public sealed record LlmReplacementCredential(string Id, string Name, string TemplateId);

public sealed record LlmReplacementReport(LlmReplacementCredential Credential, bool DryRun, IReadOnlyList<LlmReplacementItem> Items, IReadOnlyList<LlmReplacementUnresolved> Unresolved)
{
    public int NodesReplaced => Items.Sum(i => i.Replaced);
    public int ItemsChanged => Items.Count(i => i.Status == "updated");
}

/// <summary>Why a replacement could not even start (no usable model connection, an unknown credential, an unknown starting item).</summary>
public sealed class LlmReplacementException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Points the model connections of an agent, a team or a workflow — and of everything it contains, level after level — at one of the
/// user's own credentials. What is imported or downloaded carries the connection of its author, which does not exist on the
/// server that receives it: left as it is, the first run fails. Teams are followed through their members, workflows through the
/// agents, teams and workflows they call (each item only once, so a cycle ends). Only the language-model connections are touched;
/// search, e-mail and the other kinds of connection are left alone.
/// </summary>
public sealed class LlmConnectionReplacer(IEntityRepository<AgentEntity> agents, IEntityRepository<TeamEntity> teams, IEntityRepository<WorkflowEntity> workflows, CredentialsService credentials)
{
    private const string ModelNodePrefix = "ai.llm.model.";

    // Providers whose model nodes share the same inputs, so one can be turned into another. (AWS Bedrock has its own set: left alone.)
    private static readonly string[] ConvertibleProviders = ["anthropic", "openai", "gemini", "xai", "deepseek"];

    /// <summary>The credential used when none is named: the first model connection of the list that holds a key.</summary>
    public async Task<LlmReplacementCredential> ResolveCredentialAsync(string? credentialId, CancellationToken cancellationToken = default)
    {
        var all = await credentials.GetAllAsync(cancellationToken).ConfigureAwait(false);

        static bool Usable(CredentialModel c) => ConvertibleProviders.Contains(c.TemplateId, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.GetField("api_key"));

        if (!string.IsNullOrWhiteSpace(credentialId))
        {
            var named = all.FirstOrDefault(c => string.Equals(c.Id, credentialId, StringComparison.OrdinalIgnoreCase)) ?? throw new LlmReplacementException("credentials_not_found", $"Credentials '{credentialId}' not found.");
            return Usable(named) ? new LlmReplacementCredential(named.Id, named.Name, named.TemplateId.ToLowerInvariant()) : throw new LlmReplacementException("credentials_not_usable", $"Credentials '{named.Name}' are not a model connection that holds a key.");
        }

        var first = all.FirstOrDefault(Usable) ?? throw new LlmReplacementException("no_llm_credential", "There is no model connection with a key yet. Add one first.");
        return new LlmReplacementCredential(first.Id, first.Name, first.TemplateId.ToLowerInvariant());
    }

    /// <param name="roots">What was imported or chosen; everything it contains is followed.</param>
    /// <param name="credentialId">The connection to use; null for the first usable one of the list.</param>
    /// <param name="dryRun">Only report what would change.</param>
    /// <param name="scopeIds">When given, only these items are modified (an import changes what it brought, never what was already there); the rest is reported as outside the scope.</param>
    public async Task<LlmReplacementReport> ReplaceAsync(IReadOnlyList<LlmReplacementRoot> roots, string? credentialId, bool dryRun, IReadOnlySet<string>? scopeIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var credential = await ResolveCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);

        var items = new List<LlmReplacementItem>();
        var unresolved = new List<LlmReplacementUnresolved>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(LibraryEntityKind Kind, string Id, int Depth)>();
        foreach (var root in roots)
        {
            queue.Enqueue((root.Kind, root.Id, 0));
        }

        while (queue.Count > 0)
        {
            var (kind, id, depth) = queue.Dequeue();
            if (!visited.Add($"{kind}:{id}"))
            {
                continue;
            }

            var pending = new List<PendingReference>();
            switch (kind)
            {
                case LibraryEntityKind.Agent:
                {
                    var result = await agents.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    var agent = result.IsSuccess && result.Value is not null ? result.Value : throw new LlmReplacementException("entity_not_found", $"Agent '{id}' not found.");
                    var graph = ProcessGraph(agent.Graph, credential);
                    var updated = graph.Changed ? new AgentEntity(agent.Id, agent.Name, graph.Entity) { Role = agent.Role, Tags = agent.Tags, CreatedAt = agent.CreatedAt, UpdatedAt = agent.UpdatedAt } : agent;
                    items.Add(await FinishAsync(kind, agent.Id, agent.Name, depth, graph, dryRun, scopeIds, async () => (await agents.SaveAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false)).IsSuccess).ConfigureAwait(false));
                    pending.AddRange(graph.References.Select(r => new PendingReference(kind, agent.Id, agent.Name, depth, r)));
                    break;
                }
                case LibraryEntityKind.Workflow:
                {
                    var result = await workflows.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    var workflow = result.IsSuccess && result.Value is not null ? result.Value : throw new LlmReplacementException("entity_not_found", $"Workflow '{id}' not found.");
                    var graph = ProcessGraph(workflow, credential);
                    items.Add(await FinishAsync(kind, workflow.Id, workflow.Name, depth, graph, dryRun, scopeIds, async () => (await workflows.SaveAsync(graph.Entity, cancellationToken: cancellationToken).ConfigureAwait(false)).IsSuccess).ConfigureAwait(false));
                    pending.AddRange(graph.References.Select(r => new PendingReference(kind, workflow.Id, workflow.Name, depth, r)));
                    break;
                }
                default:
                {
                    var result = await teams.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    var team = result.IsSuccess && result.Value is not null ? result.Value : throw new LlmReplacementException("entity_not_found", $"Team '{id}' not found.");

                    // A team holds no model connection of its own: its members do.
                    items.Add(new LlmReplacementItem(kind, team.Id, team.Name, depth, LlmNodes: 0, Replaced: 0, ProviderChanged: false, Status: "no_llm"));
                    foreach (var member in team.Members)
                    {
                        var memberKind = member.MemberType switch
                        {
                            TeamMemberType.Team => LibraryEntityKind.Team,
                            TeamMemberType.Workflow => LibraryEntityKind.Workflow,
                            _ => LibraryEntityKind.Agent,
                        };
                        pending.Add(new PendingReference(kind, team.Id, team.Name, depth, new LibraryReference([memberKind], member.MemberEntityId)));
                    }

                    break;
                }
            }

            // What the item points to is resolved right away, so that a missing target is reported with who points to it.
            foreach (var reference in pending)
            {
                await ResolveAsync(reference, queue, unresolved, cancellationToken).ConfigureAwait(false);
            }
        }

        return new LlmReplacementReport(credential, dryRun, items, unresolved);
    }

    // ── Following references ──────────────────────────────────────────────────────────────────

    private readonly record struct PendingReference(LibraryEntityKind FromKind, string FromId, string FromName, int Depth, LibraryReference Reference);

    private async Task ResolveAsync(PendingReference pending, Queue<(LibraryEntityKind Kind, string Id, int Depth)> queue, List<LlmReplacementUnresolved> unresolved, CancellationToken cancellationToken)
    {
        var value = pending.Reference.Value.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (value.Contains("{{", StringComparison.Ordinal) || value.StartsWith('$') || value.StartsWith('='))
        {
            unresolved.Add(new LlmReplacementUnresolved(pending.FromKind, pending.FromId, pending.FromName, value, "computed_at_run_time"));
            return;
        }

        var found = await FindAsync(pending.Reference.Kinds, value, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            unresolved.Add(new LlmReplacementUnresolved(pending.FromKind, pending.FromId, pending.FromName, value, "not_found"));
            return;
        }

        queue.Enqueue((found.Value.Kind, found.Value.Id, pending.Depth + 1));
    }

    private async Task<(LibraryEntityKind Kind, string Id)?> FindAsync(IReadOnlyList<LibraryEntityKind> kinds, string value, CancellationToken cancellationToken)
    {
        var isId = Guid.TryParse(value, out _);
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case LibraryEntityKind.Agent when isId && (await agents.GetAsync(value, cancellationToken).ConfigureAwait(false)).IsSuccess:
                    return (kind, value);
                case LibraryEntityKind.Team when isId && (await teams.GetAsync(value, cancellationToken).ConfigureAwait(false)).IsSuccess:
                    return (kind, value);
                case LibraryEntityKind.Workflow:
                    if (isId && (await workflows.GetAsync(value, cancellationToken).ConfigureAwait(false)).IsSuccess)
                    {
                        return (kind, value);
                    }

                    // A nested workflow is called by its name.
                    var byName = (await workflows.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => string.Equals(w.Name, value, StringComparison.OrdinalIgnoreCase));
                    if (byName is not null)
                    {
                        return (kind, byName.Id);
                    }

                    break;
            }
        }

        return null;
    }

    private static async Task<LlmReplacementItem> FinishAsync(LibraryEntityKind kind, string id, string name, int depth, GraphResult graph, bool dryRun, IReadOnlySet<string>? scopeIds, Func<Task<bool>> save)
    {
        if (graph.LlmNodes == 0)
        {
            return new LlmReplacementItem(kind, id, name, depth, 0, 0, false, "no_llm");
        }

        if (!graph.Changed)
        {
            return new LlmReplacementItem(kind, id, name, depth, graph.LlmNodes, 0, false, "unchanged", graph.Note);
        }

        if (scopeIds is not null && !scopeIds.Contains(id))
        {
            return new LlmReplacementItem(kind, id, name, depth, graph.LlmNodes, graph.Replaced, graph.ProviderChanged, "outside_scope", "It was already there before the import: left as it is.");
        }

        if (!dryRun && !await save().ConfigureAwait(false))
        {
            return new LlmReplacementItem(kind, id, name, depth, graph.LlmNodes, graph.Replaced, graph.ProviderChanged, "failed", "It could not be saved.");
        }

        return new LlmReplacementItem(kind, id, name, depth, graph.LlmNodes, graph.Replaced, graph.ProviderChanged, "updated", graph.Note);
    }

    // ── One graph ─────────────────────────────────────────────────────────────────────────────

    private sealed record GraphResult(WorkflowEntity Entity, bool Changed, int LlmNodes, int Replaced, bool ProviderChanged, string? Note, IReadOnlyList<LibraryReference> References);

    private static GraphResult ProcessGraph(WorkflowEntity graph, LlmReplacementCredential credential)
    {
        var nodes = new List<WorkflowNodeDefinition>(graph.Nodes.Count);
        var references = new List<LibraryReference>();
        int llmNodes = 0, replaced = 0;
        var providerChanged = false;
        string? note = null;

        foreach (var node in graph.Nodes)
        {
            LibraryReferences.Collect(node, references);

            if (!node.NodeType.StartsWith(ModelNodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                nodes.Add(node);
                continue;
            }

            llmNodes++;
            var provider = node.NodeType[ModelNodePrefix.Length..].ToLowerInvariant();
            if (!ConvertibleProviders.Contains(provider))
            {
                note = $"A '{provider}' connection is left as it is: it cannot be converted.";
                nodes.Add(node);
                continue;
            }

            var inputs = node.Parameters.ValueKind == JsonValueKind.Object ? JsonNode.Parse(node.Parameters.GetRawText())!.AsObject() : new JsonObject();
            var sameCredential = inputs["credentials"] is JsonValue current && current.TryGetValue<string>(out var currentId) && string.Equals(currentId, credential.Id, StringComparison.OrdinalIgnoreCase);
            if (sameCredential && provider == credential.TemplateId)
            {
                nodes.Add(node);
                continue;
            }

            var changesProvider = provider != credential.TemplateId;
            inputs["credentials"] = credential.Id;
            if (changesProvider)
            {
                // A model name belongs to its provider: the new connection brings its own.
                inputs.Remove("model");
                providerChanged = true;
            }

            nodes.Add(
                new WorkflowNodeDefinition(node.Id, ModelNodePrefix + credential.TemplateId, JsonSerializer.SerializeToElement(inputs), node.Metadata.ToDictionary(p => p.Key, p => p.Value), node.InputsMetadata.ToDictionary(p => p.Key, p => p.Value))
                {
                    Position = node.Position,
                    ZIndex = node.ZIndex,
                }
            );
            replaced++;
        }

        return new GraphResult(replaced > 0 ? graph with { Nodes = nodes } : graph, replaced > 0, llmNodes, replaced, providerChanged, note, references);
    }
}
