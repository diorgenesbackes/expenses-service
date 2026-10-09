namespace Expenses.Application.Identity;

public enum IdentityProviderFailure
{
    UserExists,
    PasswordPolicy,
    InvalidCredentials,
    UserNotFound,
    ChallengeRequired,
    Unavailable,
    Configuration,
    InvalidResponse
}

/// <summary>Sanitized external failure; intentionally never retains the SDK exception or its payload.</summary>
public sealed class IdentityProviderException(
    IdentityProviderFailure failure, bool outcomeMayHaveChanged = false)
    : Exception($"Identity provider operation failed: {failure}.")
{
    public IdentityProviderFailure Failure { get; } = failure;
    public bool OutcomeMayHaveChanged { get; } = outcomeMayHaveChanged;
}
