using System.Text.RegularExpressions;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Services;

public sealed partial class PromptRenderer
{
    [GeneratedRegex(@"\{\{(\w+)\}\}", RegexOptions.Compiled)]
    private static partial Regex VariablePlaceholder();

    public string Render(PromptEntity PromptEntity, Dictionary<string, string> inputs)
    {
        return VariablePlaceholder()
            .Replace(
                PromptEntity.Content,
                match =>
                {
                    var variableName = match.Groups[1].Value;
                    if (inputs.TryGetValue(variableName, out var value))
                        return value;

                    var variable = PromptEntity.Variables.Find(v => string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));

                    if (variable?.DefaultValue is not null)
                        return variable.DefaultValue;

                    return match.Value;
                }
            );
    }
}
