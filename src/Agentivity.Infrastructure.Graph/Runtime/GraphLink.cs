namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// An explicit directed edge between two tasks in a graph definition.
/// </summary>
/// <remarks>
/// Links are optional metadata — the primary dependency mechanism is
/// <see cref="GraphTask.DependsOn"/>. Links add a label and explicit edge identity
/// that can be used for visualisation, observability, or conditional routing
/// at the caller layer. The <see cref="GraphRuntime"/> does not evaluate links
/// for scheduling; it reads only <see cref="GraphTask.DependsOn"/>.
/// </remarks>
/// <param name="Id">Unique identifier for this link within the graph.</param>
/// <param name="FromTaskId">Id of the source task (upstream).</param>
/// <param name="ToTaskId">Id of the target task (downstream).</param>
/// <param name="Label">Optional human-readable label describing the relationship.</param>
public sealed record GraphLink(string Id, string FromTaskId, string ToTaskId, string? Label = null);
