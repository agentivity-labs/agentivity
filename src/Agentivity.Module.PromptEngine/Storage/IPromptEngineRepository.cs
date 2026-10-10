using Agentivity.Infrastructure.Catalogue;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Storage;

/// <summary>
/// Repository for PromptEngine platform data (prompts, test cases, executions, optimization runs).
/// </summary>
public interface IPromptEngineRepository
{
    // --- PromptEntity ---
    ValueTask<PromptEntity?> GetPromptAsync(string id, CancellationToken ct = default);
    ValueTask<List<PromptEntity>> ListPromptsAsync(string? category = null, CancellationToken ct = default);
    ValueTask<CatalogBrowseResult> BrowsePromptsAsync(string? folderId = null, CancellationToken ct = default);
    ValueTask SavePromptAsync(PromptEntity PromptEntity, CancellationToken ct = default);
    ValueTask<bool> DeletePromptAsync(string id, CancellationToken ct = default);
    ValueTask SavePromptVersionSnapshotAsync(PromptVersionSnapshot snapshot, CancellationToken ct = default);
    ValueTask<PromptVersionSnapshot?> GetPromptVersionSnapshotAsync(string versionId, CancellationToken ct = default);
    ValueTask<List<PromptVersionSnapshot>> ListPromptVersionSnapshotsAsync(string promptId, CancellationToken ct = default);
    ValueTask<bool> SupportsPromptVersioningAsync(CancellationToken ct = default);
    ValueTask<PromptVersionSnapshot?> CreatePromptVersionAsync(string promptId, string versionName, string? createdBy = null, CancellationToken ct = default);
    ValueTask<PromptVersionSnapshot?> RestorePromptVersionAsync(string promptId, string versionId, string versionName, string? restoredBy = null, CancellationToken ct = default);

    // --- TestCase ---
    ValueTask<TestCase?> GetTestCaseAsync(string id, CancellationToken ct = default);
    ValueTask<List<TestCase>> ListTestCasesAsync(string promptId, CancellationToken ct = default);
    ValueTask SaveTestCaseAsync(TestCase testCase, CancellationToken ct = default);
    ValueTask<bool> DeleteTestCaseAsync(string id, CancellationToken ct = default);

    // --- TestExecution ---
    ValueTask SaveExecutionAsync(TestExecution execution, CancellationToken ct = default);
    ValueTask<List<TestExecution>> ListPlaygroundExecutionsAsync(string promptId, CancellationToken ct = default);
    ValueTask<List<TestExecution>> ListTestCaseExecutionsAsync(string promptId, CancellationToken ct = default);
    ValueTask<TestExecution?> GetExecutionAsync(string executionId, CancellationToken ct = default);

    // --- OptimizationRun ---
    ValueTask<OptimizationRun?> GetOptimizationRunAsync(string id, CancellationToken ct = default);
    ValueTask SaveOptimizationRunAsync(OptimizationRun run, CancellationToken ct = default);
}
