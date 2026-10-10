using Agentivity.Observability.Projection;

namespace Agentivity.Observability.Abstractions;

public interface IDebugTraceProjectionTarget
{
    void UpdateNodeTrace(Guid runId, string nodeId, ProjectedDebugTraceItem trace, bool notify = true);
}
