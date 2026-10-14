using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agentivity.Host.Host;

/// <summary>
/// Single entry point that converts unhandled exceptions raised by endpoint handlers
/// into <see cref="ApiErrorResponse"/> payloads. Replaces ad-hoc <c>try/catch (Exception)</c>
/// previously scattered across minimal-API endpoints.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // OperationCanceledException tied to the request abort token must not be reported as 500.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (status, code, userMessage) = exception switch
        {
            // What the client sent cannot be read (too large, malformed): its request is at fault, not the server.
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => (StatusCodes.Status413PayloadTooLarge, "request_too_large", "What was sent is too large."),
            BadHttpRequestException bad => (bad.StatusCode, "bad_request", "The request is invalid."),
            ArgumentException => (StatusCodes.Status400BadRequest, "bad_request", "The request is invalid."),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "not_found", "The requested resource was not found."),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden", "Access to this resource is denied."),
            NotSupportedException or NotImplementedException => (StatusCodes.Status501NotImplemented, "not_implemented", "This operation is not supported."),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred."),
        };

        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, exception, "Unhandled exception while processing {Method} {Path}: {ExceptionType}", httpContext.Request.Method, httpContext.Request.Path, exception.GetType().Name);

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(ApiErrors.Create(status, code, userMessage, exception.Message), cancellationToken);
        return true;
    }
}
