using System.Text.Json.Serialization;

namespace Expenses.Application.Identity;

public enum SessionFailure { InvalidInput, InvalidCredentials, Locked, Busy, Unavailable, ProfileMissing }
public sealed class SessionException(SessionFailure failure, int? remaining = null, int? retryAfter = null)
    : Exception($"Session operation failed: {failure}.")
{
    public SessionFailure Failure { get; } = failure;
    public int? Remaining { get; } = remaining;
    public int? RetryAfter { get; } = retryAfter;
}

public sealed record SessionUser(Guid Id, string Email);
public sealed record VerifiedAccess(string Subject, DateTimeOffset ExpiresAt);
public interface IAccessTokenValidator
{
    Task<VerifiedAccess> ValidateAsync(string token, CancellationToken cancellationToken);
}
public interface IEmailHasher { byte[] Hash(string normalizedEmail); }
public sealed class SessionCredentials(Guid id, string? accessToken, string refreshToken)
{
    public Guid Id { get; } = id;
    [JsonIgnore] public string? AccessToken { get; } = accessToken;
    [JsonIgnore] public string RefreshToken { get; } = refreshToken;
    public override string ToString() => "SessionCredentials [redacted]";
}
public sealed class SessionResult(SessionUser user, SessionCredentials credentials,
    DateTimeOffset accessExpiresAt, DateTimeOffset expiresAt, bool refreshed = false)
{
    public SessionUser User { get; } = user;
    [JsonIgnore] public SessionCredentials Credentials { get; } = credentials;
    public DateTimeOffset AccessExpiresAt { get; } = accessExpiresAt;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public bool Refreshed { get; } = refreshed;
}
public sealed record StoredSession(Guid Id, SessionUser User, string Subject, byte[] AccessHash,
    byte[] RefreshHash, DateTimeOffset AccessExpiresAt, DateTimeOffset ExpiresAt, string State, DateTimeOffset UpdatedAt);
public interface ISessionStore
{
    Task BeginLoginAsync(byte[] emailHash, Guid owner, DateTimeOffset now, CancellationToken cancellationToken);
    // Null subject denotes invalid credentials; operational errors use ReleaseLogin, never increment counters.
    Task<SessionUser?> FinishLoginAsync(byte[] emailHash, Guid owner, string? subject, Guid sessionId,
        byte[] accessHash, byte[] refreshHash, DateTimeOffset accessExpiresAt, DateTimeOffset now,
        Guid correlationId, CancellationToken cancellationToken);
    Task ReleaseLoginAsync(byte[] emailHash, Guid owner, CancellationToken cancellationToken);
    Task<StoredSession?> ReadAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> BeginRefreshAsync(Guid id, byte[] refreshHash, Guid owner, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> CompleteRefreshAsync(Guid id, Guid owner, byte[] accessHash, byte[] refreshHash,
        DateTimeOffset accessExpiresAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task RevokeAsync(Guid id, DateTimeOffset now, Guid correlationId, CancellationToken cancellationToken);
}
public interface ISessionService
{
    Task<SessionResult> LoginAsync(string? email, string? password, Guid correlationId, CancellationToken cancellationToken);
    Task<SessionResult> CurrentAsync(SessionCredentials credentials, Guid correlationId, CancellationToken cancellationToken);
    Task<SessionUser> AuthorizeAsync(SessionCredentials credentials, CancellationToken cancellationToken);
    Task LogoutAsync(SessionCredentials credentials, Guid correlationId, CancellationToken cancellationToken);
}
