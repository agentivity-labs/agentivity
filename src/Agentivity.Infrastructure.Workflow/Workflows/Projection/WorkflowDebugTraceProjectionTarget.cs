using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Projection;

namespace Agentivity.Infrastructure.Workflows.Projection;

/// <summary>
/// Stores debug trace data per workflow/agent step in the generic <see cref="IRepository{RunContext, RunRecord}"/>.
/// Traces are stored as opaque objects keyed by (runId, nodeId) and served by the inspector endpoint.
/// </summary>
public sealed class WorkflowDebugTraceProjectionTarget : IDebugTraceProjectionTarget
{
    private readonly IRepository<RunContext, RunRecord> _runRepository;

    public WorkflowDebugTraceProjectionTarget(IRepository<RunContext, RunRecord> runRepository)
    {
        _runRepository = runRepository;
    }

    public void UpdateNodeTrace(Guid runId, string nodeId, ProjectedDebugTraceItem trace, bool notify = true)
    {
        var ctx = _runRepository.FindAsync(runId.ToString("D")).AsTask().GetAwaiter().GetResult();
        ctx?.SetStepTrace(nodeId, ToDebugTraceItem(trace));
    }

    private static DebugTraceItem ToDebugTraceItem(ProjectedDebugTraceItem p) =>
        new(
            Kind: p.Kind,
            Name: p.Name,
            Message: p.Message,
            Status: p.Status switch
            {
                ProjectedDebugTraceStatus.Warning => DebugTraceStatus.Warning,
                ProjectedDebugTraceStatus.Error => DebugTraceStatus.Error,
                _ => DebugTraceStatus.Info,
            },
            Fields: p.Fields?.Select(f => new DebugTraceField(f.Name, f.Value)).ToList(),
            Children: p.Children?.Select(ToDebugTraceItem).ToList()
        );
}
