using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Expenses.Application.Identity;

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
        var code = unavailable ? "DEPENDENCY_UNAVAILABLE" : "UNEXPECTED_ERROR";
        if (exception is RegistrationException registration)
        {
            (status, code) = registration.Failure switch
            {
                RegistrationFailure.InvalidInput => (400, "REQUEST_VALIDATION_FAILED"),
                RegistrationFailure.PasswordPolicy => (422, "IDENTITY_PASSWORD_POLICY_VIOLATION"),
                RegistrationFailure.UserExists => (409, "IDENTITY_USER_EXISTS"),
                RegistrationFailure.KeyReused => (409, "IDEMPOTENCY_KEY_REUSED"),
                RegistrationFailure.InProgress => (409, "IDEMPOTENCY_REQUEST_IN_PROGRESS"),
                RegistrationFailure.KeyExpired => (409, "IDEMPOTENCY_KEY_EXPIRED"),
                RegistrationFailure.ReconciliationRequired => (503, "IDENTITY_RECONCILIATION_REQUIRED"),
                RegistrationFailure.Unavailable => (503, "DEPENDENCY_UNAVAILABLE"),
                _ => (500, "UNEXPECTED_ERROR")
            };
        }

        if (exception is SessionException session)
        {
            (status, code) = session.Failure switch
            {
                SessionFailure.InvalidInput => (400, "REQUEST_VALIDATION_FAILED"),
                SessionFailure.InvalidCredentials => (401, "IDENTITY_INVALID_CREDENTIALS"),
                SessionFailure.Locked => (423, "IDENTITY_LOGIN_BLOCKED"),
                SessionFailure.Busy => (409, "IDENTITY_OPERATION_IN_PROGRESS"),
                SessionFailure.ProfileMissing => (503, "IDENTITY_RECONCILIATION_REQUIRED"),
                _ => (503, "DEPENDENCY_UNAVAILABLE")
            };
            if (session.RetryAfter is not null) httpContext.Response.Headers.RetryAfter = session.RetryAfter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Exception messages can contain connection details or data: log only safe metadata.
        logger.LogError("Request failed with {exceptionType}; status {statusCode}; trace {traceId}; request {requestId}",
            exception.GetType().Name, status, System.Diagnostics.Activity.Current?.TraceId.ToString(), httpContext.TraceIdentifier);

        var problem = ApiProblems.Create(httpContext, status, code);
        if (exception is SessionException { Remaining: not null } failure)
            problem.Extensions["remainingAttempts"] = failure.Remaining;
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem,
            options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
