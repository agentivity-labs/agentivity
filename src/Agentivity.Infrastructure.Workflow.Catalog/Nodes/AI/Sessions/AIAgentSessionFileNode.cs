using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Sessions;

public sealed class AIAgentSessionFileNode : ResourceNode, IAgentSession, IAgentSessionAdmin
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _storageRoot;
    private readonly ILogger<AIAgentSessionFileNode> _logger;

    public AIAgentSessionFileNode(IOptions<WaitingRunStorageOptions> options, ILogger<AIAgentSessionFileNode> logger)
        : base("ai.sessions.file")
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storageRoot = Path.Combine(PathResolver.Resolve(options.Value.StoragePath), "agent-sessions");

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentSession);

        DefineMetadata("AI Agent Session (File)", "Stores AI agent conversation sessions as JSON files so they survive application restarts.", "ai.sessions");
        DefineEffects(NodeEffects.WritesInternal);

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

    public async ValueTask<AgentSessionState> OpenAsync(ResourceBinding binding, AgentSessionOpenRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentNodeId);

        string scope = ResolveScope(binding, request);
        string filePath = GetFilePath(request.SessionId, scope);
        if (!File.Exists(filePath))
        {
            return CreateEmptyState(request.SessionId, scope);
        }

        try
        {
            AgentSessionState? session;
            await using (FileStream stream = File.OpenRead(filePath))
            {
                session = await JsonSerializer.DeserializeAsync<AgentSessionState>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            if (session is null)
            {
                return CreateEmptyState(request.SessionId, scope);
            }

            if (IsExpired(session, binding))
            {
                DeleteFile(filePath);
                return CreateEmptyState(request.SessionId, scope);
            }

            return AgentSessionCompaction.NormalizeForRead(binding, session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AI agent session from {Path}", filePath);
            return CreateEmptyState(request.SessionId, scope);
        }
    }

    public async ValueTask SaveAsync(ResourceBinding binding, AgentSessionState session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(session);

        AgentSessionState normalized = AgentSessionCompaction.NormalizeForPersistence(binding, session) with { UpdatedAt = session.UpdatedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : session.UpdatedAt };

        string filePath = GetFilePath(normalized.SessionId, normalized.Scope);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        await using FileStream stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, normalized, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DeleteAsync(ResourceBinding binding, AgentSessionOpenRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);

        string scope = ResolveScope(binding, request);
        DeleteFile(GetFilePath(request.SessionId, scope));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<AgentSessionSummary>> ListAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);

        if (!Directory.Exists(_storageRoot))
        {
            return [];
        }

        var summaries = new List<AgentSessionSummary>();
        foreach (string filePath in Directory.EnumerateFiles(_storageRoot, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                AgentSessionState? session;
                await using (FileStream stream = File.OpenRead(filePath))
                {
                    session = await JsonSerializer.DeserializeAsync<AgentSessionState>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                }

                if (session is null)
                {
                    continue;
                }

                summaries.Add(ToSummary(session, binding));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect AI agent session file {Path}", filePath);
            }
        }

        return summaries.OrderByDescending(summary => summary.UpdatedAt).ToList();
    }

    public async ValueTask<int> PurgeExpiredAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(binding);

        if (!Directory.Exists(_storageRoot))
        {
            return 0;
        }

        int removed = 0;
        foreach (string filePath in Directory.EnumerateFiles(_storageRoot, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                AgentSessionState? session;
                await using (FileStream stream = File.OpenRead(filePath))
                {
                    session = await JsonSerializer.DeserializeAsync<AgentSessionState>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                }

                if (session is null || !IsExpired(session, binding))
                {
                    continue;
                }

                DeleteFile(filePath);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to purge AI agent session file {Path}", filePath);
            }
        }

        return removed;
    }

    private AgentSessionState CreateEmptyState(string sessionId, string scope) => new(sessionId, scope, [], new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);

    private string GetFilePath(string sessionId, string scope) => Path.Combine(_storageRoot, SanitizeSegment(sessionId), $"{SanitizeSegment(scope)}.json");

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

    private void DeleteFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete AI agent session file {Path}", filePath);
        }
    }

    private static string SanitizeSegment(string value) => string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
