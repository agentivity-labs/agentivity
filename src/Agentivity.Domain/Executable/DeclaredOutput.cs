namespace Agentivity.Domain.Executable;

/// <summary>
/// An output a workflow declares: what it gives to whoever calls it (a step of another workflow, a team), under
/// <paramref name="Name"/>. <paramref name="Value"/> says where it is taken from in the workflow, as any input of a step says it
/// (<c>{{step.field}}</c>, or a text that holds some). The output named "output" is the workflow's answer.
/// </summary>
public sealed record DeclaredOutput(string Name, string Value, string? Description = null);
