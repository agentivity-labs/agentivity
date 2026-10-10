namespace Agentivity.Module.PromptEngine.Endpoints;

internal static class PromptEngineEndpointErrors
{
    public static IResult Validation(string code, string message) => Results.BadRequest(new { code, message });

    public static IResult FromInvalidOperation(InvalidOperationException exception)
    {
        var message = exception.Message;

        if (message.Contains("missing llm_credential_id", StringComparison.OrdinalIgnoreCase))
        {
            return Validation("prompt_llm_credential_required", message);
        }

        if (message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
        {
            return Validation("llm_credential_not_found", message);
        }

        if (message.Contains("is not an llm credential", StringComparison.OrdinalIgnoreCase))
        {
            return Validation("invalid_llm_credential", message);
        }

        if (message.Contains("missing base_url", StringComparison.OrdinalIgnoreCase))
        {
            return Validation("llm_credential_base_url_required", message);
        }

        return Validation("promptengine_validation_error", message);
    }
}
