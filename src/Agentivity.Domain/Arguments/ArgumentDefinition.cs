namespace Agentivity.Domain.Arguments;

public sealed class ArgumentDefinition
{
    public string Name { get; }
    public ArgumentType Type { get; }
    public string? Description { get; }
    public object? DefaultValue { get; }
    public ArgumentEditorMode EditorMode { get; }
    public ArgumentUiMetadata? Ui { get; }
    public bool TriggersReconfigure { get; }
    public IReadOnlyList<ChoiceOption>? Choices { get; }
    public string? CredentialsTemplateId { get; }
    public object? BindingExpression { get; init; }

    /// <summary>
    /// Whether the node cannot run without a value for this input. <c>null</c> means "not declared yet" (the node has
    /// not been classified), which is deliberately different from <c>false</c> ("optional"): a completeness check can
    /// then tell what is still to be reviewed.
    /// </summary>
    public bool? Required { get; init; }

    public object? Value { get; set; }
    public Action<object?>? OnChanged { get; init; }

    public ArgumentDefinition(
        string name,
        ArgumentType type,
        string? description = null,
        object? defaultValue = null,
        object? bindingExpression = null,
        ArgumentEditorMode editorMode = ArgumentEditorMode.Auto,
        IReadOnlyList<ChoiceOption>? choices = null,
        string? credentialsTemplateId = null,
        ArgumentUiMetadata? ui = null,
        bool triggersReconfigure = false,
        Action<object?>? onChanged = null
    )
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Argument name cannot be null or whitespace.", nameof(name));

        Name = name;
        Type = type;
        Description = description;
        DefaultValue = defaultValue;
        BindingExpression = bindingExpression;
        EditorMode = editorMode;
        Choices = choices;
        CredentialsTemplateId = credentialsTemplateId;
        Ui = ui;
        TriggersReconfigure = triggersReconfigure;
        Value = defaultValue;
        OnChanged = onChanged;
    }

    public override string ToString() => $"{Name} : {Type} (value = {Value ?? "null"})";
}

public sealed record ChoiceOption(string Value, string? Label = null)
{
    public string DisplayLabel => Label ?? Value;
}
