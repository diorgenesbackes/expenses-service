using System.Security.Claims;
using System.Text.Encodings.Web;
using Expenses.Application.Identity;
using Expenses.Api.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Expenses.Api.Configuration;

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ISessionService service, SessionCookies cookies)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ExpensesSession";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null) return AuthenticateResult.NoResult();
        var credentials = cookies.Read(Context);
        if (credentials is null) return AuthenticateResult.NoResult();
        try
        {
            var user = await service.AuthorizeAsync(credentials, Context.RequestAborted);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (SessionException error) when (error.Failure == SessionFailure.InvalidCredentials)
        {
            return AuthenticateResult.Fail("Session is invalid.");
        }
        // Database/provider outages propagate as sanitized 503; never authorize from cached identity.
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => ApiProblems.WriteAsync(Context, 401, "IDENTITY_SESSION_INVALID");
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiProblems.WriteAsync(Context, 403, "RESOURCE_ACCESS_DENIED");
}
