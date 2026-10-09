namespace Expenses.Application.Identity;

public sealed class RegisterUserInput(string? email, string? password, Guid? idempotencyKey, Guid correlationId)
{
    public string? Email { get; } = email;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Password { get; } = password;
    public Guid? IdempotencyKey { get; } = idempotencyKey;
    public Guid CorrelationId { get; } = correlationId;
}

public sealed record RegisteredUser(Guid Id, string Email, DateTime CreatedAt);
public sealed record RegistrationResult(RegisteredUser User, bool Replayed = false);
public sealed record RegistrationReservation(Guid OperationId, RegistrationResult? Replay = null);

public enum RegistrationFailure
{
    InvalidInput, PasswordPolicy, UserExists, KeyReused, InProgress, KeyExpired,
    Unavailable, ReconciliationRequired, Unexpected
}

public sealed class RegistrationException(RegistrationFailure failure) : Exception($"Registration failed: {failure}.")
{
    public RegistrationFailure Failure { get; } = failure;
}

public interface IRegistrationStore
{
    Task<RegistrationReservation> ReserveAsync(string email, Guid key, Guid correlationId, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task RecordPrincipalAsync(Guid operationId, ExternalIdentity principal, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordPasswordAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<RegisteredUser> CompleteAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<RegisteredUser?> ReadCompletionAsync(Guid operationId, CancellationToken cancellationToken);
    Task RecordFailureAsync(Guid operationId, RegistrationFailure failure, bool uncertain, DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IRegistrationService
{
    Task<RegistrationResult> RegisterAsync(RegisterUserInput input, CancellationToken cancellationToken);
}

/// <summary>Safe operational signal; no credentials, email or provider exception.</summary>
public interface IRegistrationObserver
{
    void ReconciliationRequired(Guid operationId, Guid correlationId);
}
