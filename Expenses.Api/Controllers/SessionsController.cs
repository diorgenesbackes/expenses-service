using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Expenses.Api.Configuration;
using Expenses.Api.Errors;
using Expenses.Application.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Expenses.Api.Controllers;

[ApiController]
[AllowAnonymous] // Authentication and refresh are coordinated explicitly by the session use cases.
[Route("v1/identity")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SessionsController(ISessionService service, SessionCookies cookies, TimeProvider clock) : ControllerBase
{
    /// <summary>Obtém token CSRF para login, consulta/renovação e logout.</summary>
    /// <remarks>HTTPS; cookie HttpOnly e token público de proteção. Não é credencial de autenticação.</remarks>
    [HttpGet("csrf", Name = "GetIdentityCsrf")]
    [ProducesResponseType<CsrfResponse>(200)]
    public ActionResult<CsrfResponse> Csrf([FromServices] IAntiforgery antiforgery)
    {
        if (!Request.IsHttps) return StatusCode(403, ApiProblems.Create(HttpContext, 403, "IDENTITY_CSRF_INVALID"));
        return new CsrfResponse(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!);
    }

    /// <summary>Autentica e cria sessão persistente com cookies seguros.</summary>
    /// <remarks>Exige X-CSRF-Token obtido em GET /v1/identity/csrf e seu cookie. JSON até 8 KB.
    /// Dez falhas em janela móvel de 15 min bloqueiam somente o e-mail por 15 min.
    /// Avisos de 3/2/1 tentativas; 423 inclui Retry-After. Concorrência: 409; não há idempotência de login.
    /// Processamento até 5 s; chamada Cognito até 3 s. Tokens nunca aparecem no JSON.</remarks>
    [HttpPost("sessions", Name = "CreateSession")]
    [ServiceFilter(typeof(SessionCsrfFilter))]
    [Consumes("application/json")]
    [RequestSizeLimit(8192)]
    [ProducesResponseType<SessionResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(413)]
    [ProducesResponseType<ProblemDetails>(415)]
    [ProducesResponseType<ProblemDetails>(423)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<ActionResult<SessionResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await service.LoginAsync(request.Email, request.Password, Correlation, cancellationToken);
        cookies.Write(HttpContext, result);
        return ToResponse(result);
    }

    /// <summary>Consulta sessão e renova access token quando necessário.</summary>
    /// <remarks>Exige X-CSRF-Token mesmo sendo GET, pois pode rotacionar cookies. Falha de renovação encerra sessão.
    /// Renovação concorrente retorna 409 sem limpar cookies. Prazo absoluto de sete dias não é prorrogado.</remarks>
    [HttpGet("session/current", Name = "GetCurrentSession")]
    [ServiceFilter(typeof(SessionCsrfFilter))]
    [ProducesResponseType<SessionResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<ActionResult<SessionResponse>> Current(CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.CurrentAsync(RequireCredentials(), Correlation, cancellationToken);
            if (result.Refreshed) cookies.Write(HttpContext, result);
            return ToResponse(result);
        }
        catch (SessionException error) when (error.Failure != SessionFailure.Busy) { cookies.Clear(HttpContext); throw; }
    }

    /// <summary>Revoga imediatamente a sessão local e tenta revogar o refresh no Cognito.</summary>
    /// <remarks>Exige X-CSRF-Token. 204 somente após commit local; indisponibilidade local retorna 503.
    /// Revogação repetida da mesma sessão é idempotente. Requisições já autorizadas podem terminar.</remarks>
    [HttpDelete("sessions/current", Name = "DeleteCurrentSession")]
    [ServiceFilter(typeof(SessionCsrfFilter))]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        try { await service.LogoutAsync(RequireCredentials(), Correlation, cancellationToken); return NoContent(); }
        finally { cookies.Clear(HttpContext); }
    }
    private Guid Correlation => (Guid)HttpContext.Items["correlationId"]!;
    private SessionCredentials RequireCredentials() => cookies.Read(HttpContext) ?? throw new SessionException(SessionFailure.InvalidCredentials);
    private SessionResponse ToResponse(SessionResult result) => new(result.User, new SessionState(true, result.Refreshed,
        Math.Max(0, (int)(result.AccessExpiresAt - clock.GetUtcNow()).TotalSeconds),
        Math.Max(0, (int)(result.ExpiresAt - clock.GetUtcNow()).TotalSeconds)));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LoginRequest
{
    [Required, MaxLength(320)] public string? Email { get; init; }
    [Required, MaxLength(256)] public string? Password { get; init; }
}
public sealed record CsrfResponse(string Token);
public sealed record SessionResponse(SessionUser User, SessionState Session);
public sealed record SessionState(bool Authenticated, bool Refreshed, int AccessTokenExpiresInSeconds, int RefreshTokenExpiresInSeconds);
