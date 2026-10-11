using System.Text.Json.Nodes;
using Agentivity.Module.Assistant.Changesets;

namespace Agentivity.Module.Assistant.Loop;

/// <summary>What happens during one turn of the assistant, as a person following along would want to see it.</summary>
public abstract record AssistantEvent;

public sealed record RunStarted(string RunId, string ChangesetId) : AssistantEvent;

/// <summary>A piece of what the assistant says to the person.</summary>
public sealed record AssistantText(string Content) : AssistantEvent;

/// <summary>The assistant starts using a tool. <paramref name="Summary"/> says what it is doing in plain words.</summary>
public sealed record ToolStarted(string CallId, string Tool, string Summary) : AssistantEvent;

/// <summary>A tool finished. <paramref name="Summary"/> says what came of it in plain words; <paramref name="Ok"/> is false when it was refused.</summary>
public sealed record ToolFinished(string CallId, string Tool, bool Ok, string Summary) : AssistantEvent;

/// <summary>The changeset changed: what it holds now.</summary>
public sealed record ChangesetChanged(JsonObject Summary) : AssistantEvent;

/// <summary>The plan was stated or revised.</summary>
public sealed record PlanChanged(AssistantPlan Plan) : AssistantEvent;

/// <summary>The assistant needs the person to answer before it can go on. The turn ends here.</summary>
/// <summary>
/// <paramref name="Pick"/> is set when the answer is one of the person's connections: the choices then come from the platform,
/// not from the model, and the answer is a connection id.
/// </summary>
public sealed record Question(string Text, IReadOnlyList<string> Options, ConnectionChoice? Pick = null) : AssistantEvent;

public sealed record RunFinished(int Steps, int TokensUsed, string Reason, int CachedTokens = 0) : AssistantEvent;

public sealed record RunFailed(string Code, string Message) : AssistantEvent;

/// <summary>Receives the events of a turn as they happen (the HTTP stream, a test).</summary>
public interface IAssistantEventSink
{
    ValueTask EmitAsync(AssistantEvent assistantEvent, CancellationToken cancellationToken = default);
}

/// <summary>Where the person is looking when they talk to the assistant: what is selected in the editor.</summary>
public sealed record AssistantContext(string? EntityId = null, string? NodeId = null, string? Note = null);
