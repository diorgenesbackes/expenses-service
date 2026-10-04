using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Expenses.Api.Errors;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // EF Core's non-retrying execution strategy wraps transient provider failures.
        var unavailable = exception is TimeoutException
            or NpgsqlException { IsTransient: true }
            or InvalidOperationException { InnerException: NpgsqlException { IsTransient: true } };
        var status = unavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;

        // Exception messages can contain connection details or data: log only safe metadata.
        logger.LogError("Request failed with {ExceptionType}; status {StatusCode}; trace {TraceId}",
            exception.GetType().Name, status, httpContext.TraceIdentifier);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = unavailable ? "Database temporarily unavailable" : "An unexpected error occurred",
            Type = $"https://httpstatuses.com/{status}",
            Extensions = { ["traceId"] = httpContext.TraceIdentifier }
        };
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem,
            options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
