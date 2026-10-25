using Agentivity.Domain.Workflow;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Sessions;

public sealed class AIAgentSessionInMemoryNode : ResourceNode, IAgentSession, IAgentSessionAdmin
{
    // The sessions themselves live in the injected store, not here: a node instance is not what survives
    // between two calls of a node (a suspended call and its resume can be served by different instances).
    private readonly IAgentSessionStore _sessions;

    public AIAgentSessionInMemoryNode(IAgentSessionStore sessionStore)
        : base("ai.sessions.inmemory")
    {
        _sessions = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentSession);

        DefineMetadata("AI Agent Session (In-Memory)", "Keeps AI agent conversations in memory until the application restarts.", "ai.sessions", whenToUse: "To let an AI Agent remember the conversation from one run to the next, until the application restarts. Plug it into the Agent Session of an AI Agent; to keep conversations after a restart, use AI Agent Session (File).");
        DefineEffects(NodeEffects.None);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Session", ResourcePortCategory.AIAgentSession, maxConnections: -1);
        DefineChoice("mode", [("isolated", "Isolated"), ("shared", "Shared"), ("forked", "Forked")], "How the session is shared between connected agents", "isolated", required: false);
        DefineInput("max_messages", ArgumentType.Number, "Maximum number of conversation messages kept in the session", 50, required: false);
        DefineChoice("compaction_mode", [("none", "None"), ("summary", "Summary")], "Optional compaction strategy applied before persistence", "none", required: false);
        DefineChoice("tool_state_reuse_mode", [("none", "None"), ("exact_match", "Exact Match")], "Optional reuse strategy for deterministic tool results stored in session state", "none", required: false);
        DefineInput("tool_state_reuse_ttl_seconds", ArgumentType.Number, "Maximum age in seconds for reusable tool-state entries", 3600, required: false);
        DefineInput("expiration_minutes", ArgumentType.Number, "Optional session expiration in minutes (0 disables expiration)", 0, required: false);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<AgentSessionState> OpenAsync(ResourceBinding binding, AgentSessionOpenRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentNodeId);

        string scope = ResolveScope(binding, request);
        string key = CreateKey(request.SessionId, scope);

        if (_sessions.TryGet(key, out AgentSessionState? existingSession) && IsExpired(existingSession!, binding))
        {
            _ = _sessions.Remove(key);
        }

        return ValueTask.FromResult(
            _sessions.TryGet(key, out AgentSessionState? session)
                ? AgentSessionCompaction.NormalizeForRead(binding, session!)
                : new AgentSessionState(request.SessionId, scope, [], new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow)
        );
    }

    public ValueTask SaveAsync(ResourceBinding binding, AgentSessionState session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        AgentSessionState normalized = AgentSessionCompaction.NormalizeForPersistence(binding, session) with { UpdatedAt = session.UpdatedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : session.UpdatedAt };

        _sessions.Set(CreateKey(normalized.SessionId, normalized.Scope), normalized);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(ResourceBinding binding, AgentSessionOpenRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);

        string scope = ResolveScope(binding, request);
        _ = _sessions.Remove(CreateKey(request.SessionId, scope));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AgentSessionSummary>> ListAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);

        var sessions = _sessions.Snapshot().Select(entry => ToSummary(entry.Value, binding)).OrderByDescending(summary => summary.UpdatedAt).ToList();

        return ValueTask.FromResult<IReadOnlyList<AgentSessionSummary>>(sessions);
    }

    public ValueTask<int> PurgeExpiredAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);

        int removed = 0;
        foreach (KeyValuePair<string, AgentSessionState> entry in _sessions.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsExpired(entry.Value, binding))
            {
                continue;
            }

            if (_sessions.Remove(entry.Key))
            {
                removed++;
            }
        }

        return ValueTask.FromResult(removed);
    }

    private static string CreateKey(string sessionId, string scope) => $"{sessionId}::{scope}";

    private static string ResolveScope(ResourceBinding binding, AgentSessionOpenRequest request)
    {
        string mode = binding.GetString("mode")?.Trim().ToLowerInvariant() ?? "isolated";
        return mode switch
        {
            "shared" => "shared",
            "forked" => string.IsNullOrWhiteSpace(request.ScopeHint) ? request.AgentNodeId : request.ScopeHint.Trim(),
            _ => request.AgentNodeId,
        };
    }

    private static bool IsExpired(AgentSessionState session, ResourceBinding binding)
    {
        int expirationMinutes = binding.GetInt("expiration_minutes") ?? 0;
        return expirationMinutes > 0 && session.UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-expirationMinutes);
    }

    private static AgentSessionSummary ToSummary(AgentSessionState session, ResourceBinding binding) => new(session.SessionId, session.Scope, session.Messages.Count, session.ToolState.Count, session.Metadata.Count, session.UpdatedAt, IsExpired(session, binding));
}
