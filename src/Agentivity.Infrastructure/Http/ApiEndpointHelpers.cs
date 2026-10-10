using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Agentivity.Infrastructure.Http;

/// <summary>
/// Shared helper methods for returning standard API error responses. Public so any
/// Infrastructure.* project (not just Agentivity.Host) can map its own endpoints
/// with a consistent error envelope — e.g. Agentivity.Infrastructure.DataTables.
/// </summary>
public static class ApiEndpointHelpers
{
    public static IResult ApiError(int statusCode, string code, string userMessage, string detail, object? context = null) => Results.Json(ApiErrors.Create(statusCode, code, userMessage, detail, context), statusCode: statusCode);

    public static IResult ApiValidationError(string code, string userMessage, string detail, object? context = null) => ApiError(StatusCodes.Status400BadRequest, code, userMessage, detail, context);

    public static IResult ApiNotFound(string code, string userMessage, string detail, object? context = null) => ApiError(StatusCodes.Status404NotFound, code, userMessage, detail, context);

    public static IResult ApiConflictError(string code, string userMessage, string detail, object? context = null) => ApiError(StatusCodes.Status409Conflict, code, userMessage, detail, context);

    public static Task WriteApiErrorAsync(HttpResponse response, int statusCode, string code, string userMessage, string detail, CancellationToken cancellationToken, object? context = null)
    {
        response.StatusCode = statusCode;
        return response.WriteAsJsonAsync(ApiErrors.Create(statusCode, code, userMessage, detail, context), cancellationToken);
    }

    public static string SerializeStreamPayload(object payload) => JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
