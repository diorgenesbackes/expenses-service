using System.Diagnostics;
using Expenses.Api.Errors;

namespace Expenses.Api.Configuration;

internal sealed class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger,
    IWebHostEnvironment environment, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var fallback = Activity.Current is null ? new Activity("http.request").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        var header = context.Request.Headers["X-Correlation-Id"];
        var correlationId = header.Count == 1 && Guid.TryParseExact(header[0], "D", out var parsed) && parsed != Guid.Empty ? parsed : Guid.NewGuid();
        context.Items["correlationId"] = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId.ToString("D");
        context.Response.OnStarting(() =>
        {
            // ExceptionHandler clears uncommitted headers before writing its replacement response.
            context.Response.Headers["X-Correlation-Id"] = correlationId.ToString("D");
            if (context.Response.StatusCode >= 400) context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId, ["requestId"] = context.TraceIdentifier,
            ["traceId"] = Activity.Current?.TraceId.ToString(), ["spanId"] = Activity.Current?.SpanId.ToString(),
            ["serviceName"] = "expenses-api", ["environment"] = environment.EnvironmentName.ToLowerInvariant(),
            ["serviceVersion"] = typeof(Program).Assembly.GetName().Version?.ToString(),
            ["instanceId"] = Environment.MachineName
        });
        var started = Stopwatch.GetTimestamp();
        try
        {
            var usersPath = string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/v1/identity/users", StringComparison.OrdinalIgnoreCase);
            if (usersPath && HttpMethods.IsGet(context.Request.Method) && !environment.IsDevelopment())
            {
                await ApiProblems.WriteAsync(context, 404, "RESOURCE_NOT_FOUND");
                return;
            }
            var loginPath = string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/v1/identity/sessions", StringComparison.OrdinalIgnoreCase);
            if ((usersPath || loginPath) && HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers.CacheControl = "no-store";
                // Anonymous registration creates no session and ignores cookies. JSON-only plus explicit origins
                // prevents browser form submissions; authenticated session endpoints will need their own CSRF token.
                var origin = context.Request.Headers.Origin;
                if (origin.Count > 0)
                {
                    var allowed = configuration.GetSection("Identity:AllowedOrigins").Get<string[]>() ?? [];
                    var sameOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
                    if (origin.Count != 1 || origin[0] != sameOrigin && !allowed.Contains(origin[0], StringComparer.Ordinal))
                    {
                        await ApiProblems.WriteAsync(context, 403, "RESOURCE_ACCESS_DENIED");
                        return;
                    }
                }
                if (context.Request.ContentLength > 8192)
                {
                    await ApiProblems.WriteAsync(context, 413, "REQUEST_TOO_LARGE");
                    return;
                }
                // Enforce the same limit for chunked bodies and the in-process test host.
                var original = context.Request.Body;
                var buffer = new byte[8193];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await original.ReadAsync(buffer.AsMemory(count), context.RequestAborted);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 8192)
                {
                    await ApiProblems.WriteAsync(context, 413, "REQUEST_TOO_LARGE");
                    return;
                }
                using var body = new MemoryStream(buffer, 0, count, writable: false);
                context.Request.Body = body;
                try { await next(context); }
                finally { context.Request.Body = original; Array.Clear(buffer); }
                return;
            }
            await next(context);
        }
        finally
        {
            var aborted = context.RequestAborted.IsCancellationRequested;
            logger.LogInformation(new EventId(1000, "http.request.completed"),
                "HTTP request completed; {httpMethod} {route}; {statusCode}; {durationMs}; {outcome}",
                context.Request.Method, (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
                aborted ? (int?)null : context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                aborted ? "client_aborted" : context.Response.StatusCode < 400 ? "success" : "rejected");
        }
    }
}
