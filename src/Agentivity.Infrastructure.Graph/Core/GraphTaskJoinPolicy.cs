namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Controls how many predecessors must complete before a task becomes ready.
/// Default behavior (when null on a GraphTask) is All — every DependsOn must complete.
/// </summary>
public abstract record GraphTaskJoinPolicy
{
    private GraphTaskJoinPolicy() { }

    /// <summary>All predecessors must complete (default behavior).</summary>
    public sealed record All : GraphTaskJoinPolicy;

    /// <summary>Any single predecessor completing is sufficient.</summary>
    public sealed record Any : GraphTaskJoinPolicy;

    /// <summary>At least <see cref="N"/> predecessors must complete.</summary>
    public sealed record AtLeast(int N) : GraphTaskJoinPolicy;

    public static All AllOf { get; } = new All();
    public static Any AnyOf { get; } = new Any();

    public static AtLeast AtLeastN(int n) => new(n);
}
