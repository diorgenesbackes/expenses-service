using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace Expenses.Application.Identity;

public sealed class SessionService(ISessionStore store, IIdentityProvider provider, IAccessTokenValidator validator,
    IEmailHasher hasher, TimeProvider clock) : ISessionService
{
    public const string InstrumentationName = "Expenses.Identity.Sessions";
    private static readonly Meter Meter = new(InstrumentationName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("expenses.identity.session.duration", "s");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("expenses.identity.session.operations");
    private static readonly Counter<long> RevokeFailures = Meter.CreateCounter<long>("expenses.identity.session.remote_revoke.failures");
    private static readonly ActivitySource Activities = new(InstrumentationName);

    public Task<SessionResult> LoginAsync(string? email, string? password, Guid correlationId, CancellationToken cancellationToken) =>
        ExecuteAsync("login", async token =>
        {
            string normalized;
            try
            {
                // Login validates the address but must not reject a previously accepted password policy.
                normalized = RegistrationService.Validate(new RegisterUserInput(email, "Validation1", null, correlationId));
            }
            catch (RegistrationException) { throw new SessionException(SessionFailure.InvalidInput); }
            if (string.IsNullOrEmpty(password) || password.Length > 256) throw new SessionException(SessionFailure.InvalidInput);
            var hash = hasher.Hash(normalized);
            var owner = Guid.NewGuid();
            await store.BeginLoginAsync(hash, owner, clock.GetUtcNow(), token);
            IdentityTokens? tokens = null;
            try
            {
                try { tokens = await provider.AuthenticateAsync(normalized, password, token); }
                catch (IdentityProviderException error) when (error.Failure == IdentityProviderFailure.InvalidCredentials)
                {
                    await store.FinishLoginAsync(hash, owner, null, Guid.Empty, [], [], clock.GetUtcNow(),
                        clock.GetUtcNow(), correlationId, token);
                    throw new SessionException(SessionFailure.InvalidCredentials);
                }
                var verified = await validator.ValidateAsync(tokens.AccessToken, token);
                var id = Guid.NewGuid();
                var now = clock.GetUtcNow();
                var user = await store.FinishLoginAsync(hash, owner, verified.Subject, id,
                    Hash(tokens.AccessToken), Hash(tokens.RefreshToken), verified.ExpiresAt, now, correlationId, token)
                    ?? throw new SessionException(SessionFailure.ProfileMissing);
                return new SessionResult(user, new SessionCredentials(id, tokens.AccessToken, tokens.RefreshToken), verified.ExpiresAt, now.AddDays(7));
            }
            catch
            {
                // A failed login never publishes cookies; release only this owner's lease.
                // The bounded lease is the crash recovery path when the database is unavailable.
                if (!token.IsCancellationRequested)
                {
                    try { await store.ReleaseLoginAsync(hash, owner, token); } catch { }
                    if (tokens is not null) await TryRevokeRemoteAsync(tokens.RefreshToken, token);
                }
                throw;
            }
        }, cancellationToken);

    public Task<SessionResult> CurrentAsync(SessionCredentials credentials, Guid correlationId, CancellationToken cancellationToken) =>
        ExecuteAsync("current", async token =>
        {
            var session = await RequireSessionAsync(credentials, token);
            if (session.State == "refreshing")
                throw session.UpdatedAt.AddSeconds(5) <= clock.GetUtcNow() ? new SessionException(SessionFailure.InvalidCredentials)
                    : new SessionException(SessionFailure.Busy, retryAfter: 1);
            if (credentials.AccessToken is not null && session.AccessExpiresAt > clock.GetUtcNow())
            {
                await VerifyAccessAsync(credentials, session, token);
                var current = await RequireSessionAsync(credentials, token);
                if (current.State != "active") throw new SessionException(SessionFailure.Busy, retryAfter: 1);
                return Result(current, credentials);
            }
            var owner = Guid.NewGuid();
            if (!await store.BeginRefreshAsync(session.Id, session.RefreshHash, owner, clock.GetUtcNow(), token))
                throw new SessionException(SessionFailure.Busy, retryAfter: 1);
            IdentityTokens? refreshed = null;
            try
            {
                refreshed = await provider.RefreshAsync(credentials.RefreshToken, token);
                var verified = await validator.ValidateAsync(refreshed.AccessToken, token);
                if (verified.Subject != session.Subject) throw new SessionException(SessionFailure.InvalidCredentials);
                if (!await store.CompleteRefreshAsync(session.Id, owner, Hash(refreshed.AccessToken), Hash(refreshed.RefreshToken),
                    verified.ExpiresAt, clock.GetUtcNow(), token))
                    throw new SessionException(SessionFailure.InvalidCredentials);
                return new SessionResult(session.User, new SessionCredentials(session.Id, refreshed.AccessToken, refreshed.RefreshToken),
                    verified.ExpiresAt, session.ExpiresAt, true);
            }
            catch
            {
                // The durable 'refreshing' state already rejects authorization, even if this write fails.
                if (!token.IsCancellationRequested)
                {
                    try { await store.RevokeAsync(session.Id, clock.GetUtcNow(), correlationId, token); } catch { }
                    await TryRevokeRemoteAsync(refreshed?.RefreshToken ?? credentials.RefreshToken, token);
                }
                throw;
            }
        }, cancellationToken);

    public Task<SessionUser> AuthorizeAsync(SessionCredentials credentials, CancellationToken cancellationToken) =>
        ExecuteAsync("authorize", async token =>
        {
            if (credentials.AccessToken is null) throw new SessionException(SessionFailure.InvalidCredentials);
            var verified = await validator.ValidateAsync(credentials.AccessToken, token);
            // The database read is the authorization linearization point, after potentially slow JWKS retrieval.
            var session = await RequireSessionAsync(credentials, token);
            if (session.State != "active" || verified.Subject != session.Subject ||
                !CryptographicOperations.FixedTimeEquals(session.AccessHash, Hash(credentials.AccessToken)))
                throw new SessionException(SessionFailure.InvalidCredentials);
            return session.User;
        }, cancellationToken);

    public async Task LogoutAsync(SessionCredentials credentials, Guid correlationId, CancellationToken cancellationToken) =>
        await ExecuteAsync("logout", async token =>
        {
            // The protected cookie proves ownership, even after refresh rotation or expiry.
            // Revoke by session id so a refresh racing with logout can never restore the session.
            await store.RevokeAsync(credentials.Id, clock.GetUtcNow(), correlationId, token);
            await TryRevokeRemoteAsync(credentials.RefreshToken, token);
            return true;
        }, cancellationToken);

    private async Task<StoredSession> RequireSessionAsync(SessionCredentials credentials, CancellationToken token)
    {
        var session = await store.ReadAsync(credentials.Id, token);
        if (session is null || session.State == "revoked" || session.ExpiresAt <= clock.GetUtcNow() ||
            !CryptographicOperations.FixedTimeEquals(session.RefreshHash, Hash(credentials.RefreshToken)))
            throw new SessionException(SessionFailure.InvalidCredentials);
        return session;
    }
    private async Task VerifyAccessAsync(SessionCredentials credentials, StoredSession session, CancellationToken token)
    {
        if (credentials.AccessToken is null || !CryptographicOperations.FixedTimeEquals(session.AccessHash, Hash(credentials.AccessToken)))
            throw new SessionException(SessionFailure.InvalidCredentials);
        var verified = await validator.ValidateAsync(credentials.AccessToken, token);
        if (verified.Subject != session.Subject) throw new SessionException(SessionFailure.InvalidCredentials);
    }
    private static SessionResult Result(StoredSession session, SessionCredentials credentials) =>
        new(session.User, credentials, session.AccessExpiresAt, session.ExpiresAt);
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private async Task TryRevokeRemoteAsync(string refreshToken, CancellationToken token)
    {
        try { await provider.RevokeAsync(refreshToken, token); }
        catch { RevokeFailures.Add(1); } // Local revocation remains authoritative for this API.
    }
    private async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var activity = Activities.StartActivity($"identity.{operation}");
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try { return await action(linked.Token); }
        catch (SessionException error) { outcome = error.Failure.ToString(); throw; }
        catch (IdentityProviderException error)
        {
            outcome = "provider_failure";
            throw new SessionException(error.Failure == IdentityProviderFailure.InvalidCredentials
                ? SessionFailure.InvalidCredentials : SessionFailure.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { outcome = "client_aborted"; throw; }
        catch { outcome = "unavailable"; throw new SessionException(SessionFailure.Unavailable); }
        finally
        {
            var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            Events.Add(1, tags);
            if (outcome != "success") activity?.SetStatus(ActivityStatusCode.Error);
        }
    }
}
