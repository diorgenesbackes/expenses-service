using Expenses.Api.Errors;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Expenses.Api.Configuration;

public sealed class SessionCsrfFilter(IAntiforgery antiforgery, IConfiguration configuration) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var origin = http.Request.Headers.Origin;
        var allowed = configuration.GetSection("Identity:AllowedOrigins").Get<string[]>() ?? [];
        var sameOrigin = $"{http.Request.Scheme}://{http.Request.Host}";
        var originValid = origin.Count == 0 || origin.Count == 1 &&
            (origin[0] == sameOrigin || allowed.Contains(origin[0], StringComparer.Ordinal));
        var valid = false;
        if (originValid && http.Request.IsHttps && http.Request.Headers["X-CSRF-Token"].Count == 1)
        {
            // IsRequestValidAsync skips GET; this endpoint can rotate credentials, so force validation.
            try { await antiforgery.ValidateRequestAsync(http); valid = true; }
            catch (AntiforgeryValidationException) { }
        }
        if (!valid)
        {
            context.Result = new ObjectResult(ApiProblems.Create(http, 403, "IDENTITY_CSRF_INVALID"))
                { StatusCode = 403, ContentTypes = { "application/problem+json" } };
        }
    }
}
