namespace Agentivity.Infrastructure.AgUi;

/// <summary>
/// Describes a single widget type within a bundle.
/// <see cref="TypeKey"/> must match the key used in the Flutter <c>AgUiWidgetRegistry</c>
/// of the corresponding Flutter package.
/// </summary>
/// <param name="ParametersSchema">
/// JSON Schema string of every prop this widget accepts — must match what the React/Flutter
/// component expects exactly (no translation layer). Mandatory for every catalog widget, not
/// just the ones with <see cref="RequiresHumanResponse"/> set: a display-only widget offered
/// with nothing but a one-line description still gets guessed at blind (confirmed in
/// production — <c>KeyValue</c> rendered an empty shell because the agent invented an object
/// shape for <c>items</c> instead of the expected array of <c>{key, value}</c> pairs). Whether a
/// widget's schema is surfaced through the strict <c>interact_with_human</c> tool call or through
/// the plainer "visual widgets" system-prompt listing depends on <see cref="RequiresHumanResponse"/>,
/// not on whether a schema exists.
/// </param>
/// <param name="RequiresHumanResponse">
/// <c>true</c> when the widget has a submit action (the Flutter widget calls
/// <c>__onSubmit</c>) — invoking it as a tool call implies pausing the run for a human reply.
/// <c>false</c> for display-only widgets (e.g. <c>SummaryCard</c>, charts).
/// </param>
public sealed record AgUiWidgetDescriptor(string TypeKey, string Category, string Description, string ParametersSchema, bool RequiresHumanResponse = false);

/// <summary>
/// A named, versioned set of AG-UI widget types defined and maintained by the platform.
/// Bundles are registered at startup; the frontend can only enable or disable them.
/// </summary>
public sealed record AgUiWidgetBundle(string Id, string DisplayName, string Version, string Description, IReadOnlyList<AgUiWidgetDescriptor> Widgets);
