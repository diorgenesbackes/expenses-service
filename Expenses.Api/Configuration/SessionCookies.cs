using System.Text.Json;
using System.Security.Cryptography;
using Expenses.Application.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace Expenses.Api.Configuration;

public sealed class SessionCookies(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Expenses.Identity.SessionCookie.v1");
    private readonly ChunkingCookieManager manager = new();
    public SessionCredentials? Read(HttpContext context)
    {
        var raw = manager.GetRequestCookie(context, "cc_rt");
        if (string.IsNullOrEmpty(raw) || raw.Length > 16384) return null;
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(raw));
            return ticket is null || ticket.Id == Guid.Empty || string.IsNullOrEmpty(ticket.RefreshToken) ? null
                : new SessionCredentials(ticket.Id, manager.GetRequestCookie(context, "cc_at"), ticket.RefreshToken);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { return null; }
    }
    public void Write(HttpContext context, SessionResult session)
    {
        manager.AppendResponseCookie(context, "cc_at", session.Credentials.AccessToken!, Options(session.AccessExpiresAt));
        manager.AppendResponseCookie(context, "cc_rt", protector.Protect(JsonSerializer.Serialize(
            new Ticket(session.Credentials.Id, session.Credentials.RefreshToken))), Options(session.ExpiresAt));
    }
    public void Clear(HttpContext context)
    {
        // ExceptionHandler replaces headers when writing ProblemDetails; expire cookies at response start.
        context.Response.OnStarting(() =>
        {
            manager.DeleteCookie(context, "cc_at", Options(null));
            manager.DeleteCookie(context, "cc_rt", Options(null));
            return Task.CompletedTask;
        });
    }
    private CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true,
        Expires = expires, MaxAge = expires is null ? null : TimeSpan.FromSeconds(Math.Max(0, (expires.Value - clock.GetUtcNow()).TotalSeconds))
    };
    private sealed record Ticket(Guid Id, string RefreshToken);
}
