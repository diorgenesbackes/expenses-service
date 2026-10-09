using Expenses.Application.Identity;

namespace Expenses.Test.Support;

internal sealed class StubIdentityProvider : IIdentityProvider
{
    public int Creates;
    public int Deletes;
    public int PasswordChanges;
    public Exception? CreateFailure { get; set; }
    public Exception? PasswordFailure { get; set; }
    public Exception? DeleteFailure { get; set; }
    public Func<CancellationToken, Task>? BeforeCreate { get; set; }
    public string? LastEmail { get; private set; }
    public ExternalIdentity? Principal { get; private set; }
    public async Task<ExternalIdentity> CreateUserAsync(string email, string temporaryPassword, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Creates);
        LastEmail = email;
        if (BeforeCreate is not null) await BeforeCreate(cancellationToken);
        if (CreateFailure is not null) throw CreateFailure;
        Principal = new ExternalIdentity(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), email);
        return Principal;
    }
    public Task SetPermanentPasswordAsync(string username, string password, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref PasswordChanges);
        return PasswordFailure is null ? Task.CompletedTask : Task.FromException(PasswordFailure);
    }
    public Task<ExternalIdentity?> FindUserAsync(string username, CancellationToken cancellationToken) => Task.FromResult(Principal);
    public Task DeleteUserAsync(string username, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Deletes);
        return DeleteFailure is null ? Task.CompletedTask : Task.FromException(DeleteFailure);
    }
    public Task<IdentityTokens> AuthenticateAsync(string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RevokeAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class StubRegistrationStore : IRegistrationStore
{
    public int Reservations;
    public bool? Uncertain { get; private set; }
    public RegistrationResult? Replay { get; set; }
    public Exception? PrincipalFailure { get; set; }
    public Exception? CompleteFailure { get; set; }
    public bool CommittedDespiteFailure { get; set; }
    public readonly RegisteredUser User = new(Guid.NewGuid(), "person@example.test", DateTime.UtcNow);
    public Task<RegistrationReservation> ReserveAsync(string email, Guid key, Guid correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Reservations++;
        return Task.FromResult(new RegistrationReservation(Guid.NewGuid(), Replay));
    }
    public Task RecordPrincipalAsync(Guid operationId, ExternalIdentity principal, DateTimeOffset now, CancellationToken cancellationToken) =>
        PrincipalFailure is null ? Task.CompletedTask : Task.FromException(PrincipalFailure);
    public Task RecordPasswordAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<RegisteredUser> CompleteAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        CompleteFailure is null ? Task.FromResult(User) : Task.FromException<RegisteredUser>(CompleteFailure);
    public Task<RegisteredUser?> ReadCompletionAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult(CommittedDespiteFailure ? User : null);
    public Task RecordFailureAsync(Guid operationId, RegistrationFailure failure, bool uncertain, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uncertain = uncertain;
        return Task.CompletedTask;
    }
}

internal sealed class StubRegistrationObserver : IRegistrationObserver
{
    public int Signals;
    public void ReconciliationRequired(Guid operationId, Guid correlationId) => Signals++;
}
