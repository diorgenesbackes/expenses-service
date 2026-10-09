using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Expenses.Api.Errors;

internal static class ApiProblems
{
    public static ProblemDetails Create(HttpContext context, int status, string code)
    {
        var problem = new ProblemDetails { Status = status, Type = "about:blank", Title = ReasonPhrases.GetReasonPhrase(status) };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString();
        problem.Extensions["correlationId"] = context.Items["correlationId"]?.ToString();
        return problem;
    }

    public static Task WriteAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(Create(context, status, code), options: null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
