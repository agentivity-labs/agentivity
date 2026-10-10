namespace Agentivity.Infrastructure.Interactions.Chat;

/// <summary>
/// Describes a visual widget available in a chat channel.
/// Passed to <see cref="ChatChannel"/> so it can include the widget vocabulary
/// in the LLM's system prompt, enabling the agent to use rich widget responses.
/// </summary>
/// <param name="ParametersSchema">
/// JSON Schema string of every prop this widget accepts, printed alongside its description in
/// the "visual widgets" system-prompt listing — without it, an agent asked to emit a widget it
/// has no schema for guesses a plausible-but-wrong shape and renders an empty/broken widget.
/// </param>
public sealed record ChatChannelWidget(string TypeKey, string Description, string ParametersSchema);
