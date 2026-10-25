using System.Text.Json;
using Agentivity.Infrastructure.Catalogue;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Module.PromptEngine.Models;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.PromptEngine.Storage;

/// <summary>
/// <see cref="IPromptEngineRepository"/> backed by <see cref="IEntityRepository{PromptEntity}"/>
/// (CRUD/folders/browse for prompts) plus <see cref="IWorkspaceObjectStore"/> directly for two
/// flat, non-foldered object types on the same shared store instance:
/// <list type="bullet">
///   <item><c>prompt</c> — prompt entities, with folder/browse support and embedded version history</item>
///   <item><c>prompt.testcase</c> — test cases (flat, keyed by id)</item>
///   <item><c>prompt.optrun</c> — optimization runs (flat, keyed by id)</item>
/// </list>
/// Test executions are ephemeral and stored in a separate JSON cache file.
/// </summary>
public sealed class WorkspacePromptRepository : IPromptEngineRepository
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    internal const string PromptType = "prompt";
    internal const string TestCaseType = "prompt.testcase";
    internal const string OptRunType = "prompt.optrun";

    /// <summary>
    /// The types of the prompts domain, told apart where they were kept without them: a prompt says its kind; an optimisation run
    /// has its current iteration, a test case its expected output.
    /// </summary>
    internal static readonly WorkspaceDomainTypes DomainTypes = new([PromptType, TestCaseType, OptRunType], static (_, content) =>
        content.ValueKind != System.Text.Json.JsonValueKind.Object ? null
        : content.TryGetProperty("current_iteration", out System.Text.Json.JsonElement _) ? OptRunType
        : content.TryGetProperty("expected_output", out System.Text.Json.JsonElement _) ? TestCaseType
        : null, SpaceScoped: true);

    private readonly IEntityRepository<PromptEntity> _prompts;
    private readonly IWorkspaceObjectStore _store;
    private readonly IWorkspaceObjectVersionStore _versionStore;
    private readonly string _cachePath;
    private readonly ILogger<WorkspacePromptRepository> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public WorkspacePromptRepository(IEntityRepository<PromptEntity> prompts, IWorkspaceObjectStore store, IWorkspaceObjectVersionStore versionStore, string cachePath, ILogger<WorkspacePromptRepository> logger)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _versionStore = versionStore ?? throw new ArgumentNullException(nameof(versionStore));
        _cachePath = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Directory.CreateDirectory(_cachePath);
    }

    // ── PromptEntity ──────────────────────────────────────────────────────

    public async ValueTask<PromptEntity?> GetPromptAsync(string id, CancellationToken ct = default)
    {
        var result = await _prompts.GetAsync(id, ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }

    public async ValueTask<List<PromptEntity>> ListPromptsAsync(string? category = null, CancellationToken ct = default)
    {
        var prompts = (await _prompts.ListAsync(cancellationToken: ct).ConfigureAwait(false)).AsEnumerable();

        if (!string.IsNullOrWhiteSpace(category))
            prompts = prompts.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase));

        return prompts.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async ValueTask<CatalogBrowseResult> BrowsePromptsAsync(string? folderId = null, CancellationToken ct = default)
    {
        var raw = await _prompts.BrowseAsync(folderId, ct).ConfigureAwait(false);

        // The generic repository's CatalogBrowseItem.Role carries Entity.Role, which prompts don't
        // use — Prompt Studio expects the prompt's Category there instead (pre-existing wire
        // contract), so re-map using the entities in this folder.
        var categoriesById = (await _prompts.ListAsync(folderId, ct).ConfigureAwait(false)).ToDictionary(p => p.Id, p => p.Category, StringComparer.Ordinal);
        var items = raw.Items.Select(i => i with { Type = "PromptEntity", Role = categoriesById.GetValueOrDefault(i.Id) }).ToList();

        return raw with { Items = items };
    }

    public async ValueTask SavePromptAsync(PromptEntity prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var result = await _prompts.SaveAsync(prompt, cancellationToken: ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            _logger.LogWarning("Failed to save prompt {Id}: {Error}", prompt.Id, result.Error);
    }

    public async ValueTask<bool> DeletePromptAsync(string id, CancellationToken ct = default)
    {
        var result = await _prompts.DeleteAsync(id, ct).ConfigureAwait(false);
        return result.IsSuccess;
    }

    // ── PromptVersionSnapshot (stored inline on the prompt entity) ────────
    // Versions live in PromptEntity.Versions, so they round-trip as part of the same
    // WorkspaceEntityRepository<PromptEntity> save/load — no separate document/envelope needed.

    public async ValueTask SavePromptVersionSnapshotAsync(PromptVersionSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var prompt = await GetPromptAsync(snapshot.PromptId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Prompt '{snapshot.PromptId}' not found.");

        prompt.Versions.RemoveAll(v => v.Id == snapshot.Id);
        prompt.Versions.Add(snapshot);
        await SavePromptAsync(prompt, ct).ConfigureAwait(false);
    }

    public async ValueTask<PromptVersionSnapshot?> GetPromptVersionSnapshotAsync(string versionId, CancellationToken ct = default)
    {
        var prompts = await _prompts.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        foreach (var prompt in prompts)
        {
            var hit = prompt.Versions.FirstOrDefault(v => v.Id == versionId);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    public async ValueTask<List<PromptVersionSnapshot>> ListPromptVersionSnapshotsAsync(string promptId, CancellationToken ct = default)
    {
        var prompt = await GetPromptAsync(promptId, ct).ConfigureAwait(false);
        return prompt?.Versions.OrderByDescending(v => v.VersionNumber).ToList() ?? [];
    }

    public ValueTask<bool> SupportsPromptVersioningAsync(CancellationToken ct = default) => ValueTask.FromResult(_versionStore.VersioningCapabilities.Enabled);

    public async ValueTask<PromptVersionSnapshot?> CreatePromptVersionAsync(string promptId, string versionName, string? createdBy = null, CancellationToken ct = default)
    {
        var prompt = await GetPromptAsync(promptId, ct).ConfigureAwait(false);
        if (prompt is null)
            return null;

        prompt.Version++;
        prompt.VersionName = versionName;
        prompt.UpdatedAt = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(prompt, prompt.Version, versionName);
        prompt.CurrentVersionSnapshotId = snapshot.Id;

        // Mutate the same in-memory entity and save once — SavePromptVersionSnapshotAsync would
        // re-fetch its own copy, and a subsequent SavePromptAsync(prompt) here would then overwrite
        // that copy's just-added version with this stale one.
        prompt.Versions.RemoveAll(v => v.Id == snapshot.Id);
        prompt.Versions.Add(snapshot);
        await SavePromptAsync(prompt, ct).ConfigureAwait(false);
        return snapshot;
    }

    public async ValueTask<PromptVersionSnapshot?> RestorePromptVersionAsync(string promptId, string versionId, string versionName, string? restoredBy = null, CancellationToken ct = default)
    {
        var prompt = await GetPromptAsync(promptId, ct).ConfigureAwait(false);
        if (prompt is null)
            return null;

        var versions = await ListPromptVersionSnapshotsAsync(promptId, ct).ConfigureAwait(false);
        var snapshot = versions.FirstOrDefault(v => v.Id == versionId);
        if (snapshot is null)
            return null;

        prompt.Content = snapshot.Content;
        prompt.SystemPrompt = snapshot.SystemPrompt;
        prompt.ResponseSchema = snapshot.ResponseSchema;
        prompt.EvaluationPrompt = snapshot.EvaluationPrompt;
        prompt.LlmCredentialId = snapshot.LlmCredentialId;
        prompt.Variables = snapshot
            .Variables.Select(v => new PromptVariable
            {
                Name = v.Name,
                Description = v.Description,
                DefaultValue = v.DefaultValue,
                Required = v.Required,
            })
            .ToList();
        prompt.Metadata = new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase);
        prompt.Version++;
        prompt.VersionName = versionName;
        prompt.UpdatedAt = DateTimeOffset.UtcNow;
        prompt.CurrentVersionSnapshotId = snapshot.Id;

        await SavePromptAsync(prompt, ct).ConfigureAwait(false);
        return snapshot;
    }

    // ── TestCase ──────────────────────────────────────────────────────────

    public async ValueTask<TestCase?> GetTestCaseAsync(string id, CancellationToken ct = default)
    {
        var result = await _store.GetAsync(TestCaseType, id, ct).ConfigureAwait(false);
        return result.Success && result.Value is not null ? Deserialize<TestCase>(result.Value.Payload) : null;
    }

    public async ValueTask<List<TestCase>> ListTestCasesAsync(string promptId, CancellationToken ct = default)
    {
        var envelopes = await _store.ListAsync(TestCaseType, query: null, ct).ConfigureAwait(false);
        return envelopes.Select(e => Deserialize<TestCase>(e.Payload)).OfType<TestCase>().Where(tc => string.Equals(tc.PromptId, promptId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async ValueTask SaveTestCaseAsync(TestCase testCase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        var now = DateTimeOffset.UtcNow;
        var payload = Serialize(testCase);
        var meta = new WorkspaceObjectMetadata(CreatedAtUtc: testCase.CreatedAt, UpdatedAtUtc: now);
        var envelope = WorkspaceObjectEnvelope.CreateJson(TestCaseType, testCase.Id, testCase.Name, payload, meta);
        await _store.SaveAsync(envelope, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteTestCaseAsync(string id, CancellationToken ct = default)
    {
        var result = await _store.DeleteAsync(TestCaseType, id, ct).ConfigureAwait(false);
        return result.Success;
    }

    // ── TestExecution (JSON cache file, one file per prompt) ─────────────

    public async ValueTask SaveExecutionAsync(TestExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadExecutionCacheAsync(execution.PromptId, ct).ConfigureAwait(false);
            cache.RemoveAll(e => e.Id == execution.Id);
            cache.Add(execution);
            await SaveExecutionCacheAsync(execution.PromptId, cache, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async ValueTask<List<TestExecution>> ListPlaygroundExecutionsAsync(string promptId, CancellationToken ct = default)
    {
        var cache = await LoadExecutionCacheAsync(promptId, ct).ConfigureAwait(false);
        return cache.Where(e => !string.Equals(e.ExecutionKind, "test-case", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.ExecutedAt).ToList();
    }

    public async ValueTask<List<TestExecution>> ListTestCaseExecutionsAsync(string promptId, CancellationToken ct = default)
    {
        var cache = await LoadExecutionCacheAsync(promptId, ct).ConfigureAwait(false);
        return cache.Where(e => string.Equals(e.ExecutionKind, "test-case", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.ExecutedAt).ToList();
    }

    public async ValueTask<TestExecution?> GetExecutionAsync(string executionId, CancellationToken ct = default)
    {
        foreach (var filePath in Directory.EnumerateFiles(_cachePath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var cache = await LoadExecutionCacheFromFileAsync(filePath, ct).ConfigureAwait(false);
            var hit = cache.FirstOrDefault(e => e.Id == executionId);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    // ── OptimizationRun ───────────────────────────────────────────────────

    public async ValueTask<OptimizationRun?> GetOptimizationRunAsync(string id, CancellationToken ct = default)
    {
        var result = await _store.GetAsync(OptRunType, id, ct).ConfigureAwait(false);
        return result.Success && result.Value is not null ? Deserialize<OptimizationRun>(result.Value.Payload) : null;
    }

    public async ValueTask SaveOptimizationRunAsync(OptimizationRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var now = DateTimeOffset.UtcNow;
        var payload = Serialize(run);
        var meta = new WorkspaceObjectMetadata(CreatedAtUtc: run.StartedAt, UpdatedAtUtc: run.CompletedAt.HasValue ? new DateTimeOffset(run.CompletedAt.Value, TimeSpan.Zero) : now);
        var name = $"{run.PromptId}/{run.Id}";
        var envelope = WorkspaceObjectEnvelope.CreateJson(OptRunType, run.Id, name, payload, meta);
        await _store.SaveAsync(envelope, ct).ConfigureAwait(false);
    }

    // ── Execution cache helpers ───────────────────────────────────────────

    private async ValueTask<List<TestExecution>> LoadExecutionCacheAsync(string promptId, CancellationToken ct) => await LoadExecutionCacheFromFileAsync(GetCacheFilePath(promptId), ct).ConfigureAwait(false);

    private async ValueTask<List<TestExecution>> LoadExecutionCacheFromFileAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return [];
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return Deserialize<List<TestExecution>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load execution cache from {File}", filePath);
            return [];
        }
    }

    private async ValueTask SaveExecutionCacheAsync(string promptId, List<TestExecution> executions, CancellationToken ct)
    {
        var filePath = GetCacheFilePath(promptId);
        var tempFile = filePath + ".tmp";
        await File.WriteAllTextAsync(tempFile, Serialize(executions), ct).ConfigureAwait(false);
        File.Move(tempFile, filePath, overwrite: true);
    }

    private string GetCacheFilePath(string promptId) => Path.Combine(_cachePath, $"{promptId}.json");

    // ── Serialization ─────────────────────────────────────────────────────

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, _json);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, _json);

    private static PromptVersionSnapshot CreateSnapshot(PromptEntity p, int versionNumber, string? versionName) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            PromptId = p.Id,
            VersionNumber = versionNumber,
            VersionName = string.IsNullOrWhiteSpace(versionName) ? $"v{versionNumber}" : versionName,
            Content = p.Content,
            SystemPrompt = p.SystemPrompt,
            ResponseSchema = p.ResponseSchema,
            EvaluationPrompt = p.EvaluationPrompt,
            LlmCredentialId = p.LlmCredentialId,
            Variables = p
                .Variables.Select(v => new PromptVariable
                {
                    Name = v.Name,
                    Description = v.Description,
                    DefaultValue = v.DefaultValue,
                    Required = v.Required,
                })
                .ToList(),
            Metadata = new Dictionary<string, string>(p.Metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = DateTime.UtcNow,
        };
}
