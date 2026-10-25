using Agentivity.Infrastructure.Arguments;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Forwarding alias — logic lives in <see cref="ArgumentValueResolver"/>.
/// </summary>
[Obsolete("Use ArgumentValueResolver directly.")]
public static class WorkflowValueResolver
{
    public static object? Resolve(object? value, IDictionary<string, object?> state) => ArgumentValueResolver.Resolve(value, state);
}
