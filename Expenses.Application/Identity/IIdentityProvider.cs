namespace Expenses.Application.Identity;

/// <summary>
/// External identity operations. Registration orchestration must persist its operation before
/// creating a principal and handle compensation; creation alone does not confirm registration.
/// Passwords and tokens are transient server-side values, never HTTP response models.
/// </summary>
public interface IIdentityProvider
{
    Task<ExternalIdentity> CreateUserAsync(string email, string temporaryPassword, CancellationToken cancellationToken);
    Task SetPermanentPasswordAsync(string username, string password, CancellationToken cancellationToken);
    Task<ExternalIdentity?> FindUserAsync(string username, CancellationToken cancellationToken);
    Task<IdentityTokens> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);
    Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken);

    // Compensation must use the immutable username returned by creation, never an email alias.
    Task DeleteUserAsync(string username, CancellationToken cancellationToken);
}

public sealed record ExternalIdentity(string Username, string Subject, string Email);

/// <summary>Opaque provider credentials. Never serialize or log these values.</summary>
public sealed class IdentityTokens(string accessToken, string refreshToken, int expiresInSeconds)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccessToken { get; } = accessToken;

    [System.Text.Json.Serialization.JsonIgnore]
    public string RefreshToken { get; } = refreshToken;

    public int ExpiresInSeconds { get; } = expiresInSeconds;

    public override string ToString() => "IdentityTokens [redacted]";
}
