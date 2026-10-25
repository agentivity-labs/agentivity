namespace Agentivity.Infrastructure.Graph;

public abstract record GraphTaskActivationCondition(string DependencyTaskId)
{
    /// <summary>Activate when predecessor is Completed (mirrors the standard DependsOn behavior).</summary>
    public sealed record Always(string DependencyTaskId) : GraphTaskActivationCondition(DependencyTaskId);

    /// <summary>Activate when predecessor is Completed AND the output satisfies the predicate.</summary>
    public sealed record WhenOutput(string DependencyTaskId, Func<object?, bool> Predicate) : GraphTaskActivationCondition(DependencyTaskId);

    /// <summary>Activate when predecessor is Ignored or Skipped (the "else" branch of a conditional).</summary>
    public sealed record WhenIgnored(string DependencyTaskId) : GraphTaskActivationCondition(DependencyTaskId);
}
