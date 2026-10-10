namespace Agentivity.Infrastructure.Http;

/// <summary>
/// Standard API error payload returned alongside an HTTP status code.
/// </summary>
public sealed record ApiErrorResponse
{
    public string Status { get; init; } = "failed";

    public required int HttpStatus { get; init; }

    public required string Code { get; init; }

    public required string UserMessage { get; init; }

    public required string Detail { get; init; }

    public object? Context { get; init; }
}

public static class ApiErrors
{
    public static ApiErrorResponse Create(int httpStatus, string code, string userMessage, string detail, object? context = null) =>
        new()
        {
            HttpStatus = httpStatus,
            Code = code,
            UserMessage = userMessage,
            Detail = detail,
            Context = context,
        };
}
