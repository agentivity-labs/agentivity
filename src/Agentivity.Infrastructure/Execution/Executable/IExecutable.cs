using Agentivity.Domain.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Execution.Executable;

/// <summary>
/// Universal execution protocol implemented by each engine via a thin adapter.
/// Common interconnection contract between agent, team, and workflow engines.
/// </summary>
public interface IExecutable
{
    /// <summary>Self-describing definition of the executable (agent, team, or workflow).</summary>
    IExecutableDefinition Definition { get; }

    /// <summary>
    /// Executes the definition with the given arguments and the propagated run context.
    /// </summary>
    ValueTask<ExecutorResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default);
}
