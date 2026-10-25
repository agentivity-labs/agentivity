using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflows;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Services.WorkflowManagement;

/// <summary>
/// Service for managing workflow definitions. Backed by the generic
/// <see cref="IEntityRepository{WorkflowEntity}"/> (objectType "workflow.definition") — public
/// surface kept identical to before this repository was generalized, to limit the blast radius
/// on callers.
/// </summary>
public sealed class WorkflowService
{
    private readonly IEntityRepository<WorkflowEntity> _store;
    private readonly ILogger<WorkflowService> _logger;

    public WorkflowService(IEntityRepository<WorkflowEntity> store, ILogger<WorkflowService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Lists all workflow IDs.
    /// </summary>
    public async ValueTask<IReadOnlyCollection<string>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing all workflows");
        var entities = await _store.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return entities.Select(e => e.Id).ToArray();
    }

    /// <summary>
    /// Gets a workflow definition by ID.
    /// </summary>
    public async ValueTask<WorkflowEntity?> GetByIdAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            _logger.LogWarning("GetByIdAsync called with null or empty workflowId");
            return null;
        }

        _logger.LogDebug("Loading workflow: {WorkflowId}", workflowId);
        var result = await _store.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            if (IsMissingWorkflowError(result.Error))
            {
                _logger.LogDebug("Workflow {WorkflowId} was not found: {Error}", workflowId, result.Error);
            }
            else
            {
                _logger.LogWarning("Failed to load workflow {WorkflowId}: {Error}", workflowId, result.Error);
            }

            return null;
        }

        return result.Value;
    }

    /// <summary>
    /// Saves a workflow definition.
    /// </summary>
    public async ValueTask<OperationResult> SaveAsync(WorkflowEntity definition, CancellationToken cancellationToken = default)
    {
        if (definition == null)
        {
            return OperationResult.Failure("Workflow definition cannot be null");
        }

        _logger.LogInformation("Saving workflow: {WorkflowId}", definition.Id);
        var result = await _store.SaveAsync(definition, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _logger.LogInformation("Successfully saved workflow: {WorkflowId}", definition.Id);
        }
        else
        {
            _logger.LogError("Failed to save workflow {WorkflowId}: {Error}", definition.Id, result.Error);
        }

        return result.IsSuccess ? OperationResult.Success(result.Value?.Id) : OperationResult.Failure(result.Error!);
    }

    /// <summary>
    /// Gets version information for a workflow.
    /// </summary>
    public async ValueTask<WorkflowVersionInfo?> GetVersionAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            _logger.LogWarning("GetVersionAsync called with null or empty workflowId");
            return null;
        }

        var result = await _store.GetVersionAsync(workflowId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            _logger.LogWarning("Failed to get version for workflow {WorkflowId}: {Error}", workflowId, result.Error);
            return null;
        }

        return new WorkflowVersionInfo(result.Value!.Id, result.Value.UpdatedAt);
    }

    /// <summary>
    /// Checks if a workflow exists.
    /// </summary>
    public async ValueTask<bool> ExistsAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return false;
        }

        var workflow = await GetByIdAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return workflow != null;
    }

    private static bool IsMissingWorkflowError(string? error) => !string.IsNullOrWhiteSpace(error) && (error.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deletes a workflow definition.
    /// </summary>
    public async ValueTask<OperationResult> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return OperationResult.Failure("Workflow id is required");
        }

        _logger.LogInformation("Deleting workflow: {WorkflowId}", workflowId);
        var result = await _store.DeleteAsync(workflowId, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _logger.LogInformation("Successfully deleted workflow: {WorkflowId}", workflowId);
        }
        else
        {
            _logger.LogError("Failed to delete workflow {WorkflowId}: {Error}", workflowId, result.Error);
        }

        return result;
    }
}

/// <summary>Lightweight version info for the current state of a workflow (public surface kept identical to before the generic repository migration).</summary>
public sealed record WorkflowVersionInfo(string Id, DateTimeOffset? UpdatedAt);
