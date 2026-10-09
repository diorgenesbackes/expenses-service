using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Mail;

namespace Expenses.Application.Identity;

public sealed class RegistrationService(IRegistrationStore store, IIdentityProvider provider,
    TimeProvider clock, IRegistrationObserver observer) : IRegistrationService
{
    public const string InstrumentationName = "Expenses.Identity.Registration";
    private static readonly ActivitySource Activities = new(InstrumentationName);
    private static readonly Meter Meter = new(InstrumentationName);
    private static readonly Counter<long> Created = Meter.CreateCounter<long>("expenses.identity.user.created");
    private static readonly Counter<long> Reconciliations = Meter.CreateCounter<long>("expenses.identity.profile.reconciliation.needed");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("expenses.identity.registration.duration", "s");

    public async Task<RegistrationResult> RegisterAsync(RegisterUserInput input, CancellationToken cancellationToken)
    {
        using var activity = Activities.StartActivity("identity.registration");
        var started = Stopwatch.GetTimestamp();
        var outcome = "failure";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        Guid? operationId = null;
        ExternalIdentity? principal = null;
        var createStarted = false;
        var completionStarted = false;
        try
        {
            var email = Validate(input);
            var reservation = await store.ReserveAsync(email, input.IdempotencyKey ?? Guid.NewGuid(),
                input.CorrelationId, clock.GetUtcNow(), request.Token);
            if (reservation.Replay is not null)
            {
                outcome = "idempotent_replay";
                return reservation.Replay;
            }
            operationId = reservation.OperationId;
            createStarted = true;
            principal = await provider.CreateUserAsync(email, input.Password!, request.Token);
            await store.RecordPrincipalAsync(operationId.Value, principal, clock.GetUtcNow(), request.Token);
            await provider.SetPermanentPasswordAsync(principal.Username, input.Password!, request.Token);
            await store.RecordPasswordAsync(operationId.Value, clock.GetUtcNow(), request.Token);
            completionStarted = true;
            var user = await store.CompleteAsync(operationId.Value, clock.GetUtcNow(), request.Token);
            Created.Add(1);
            outcome = "success";
            return new RegistrationResult(user);
        }
        catch (Exception exception)
        {
            var failure = Classify(exception);
            var uncertain = createStarted;
            if (operationId.HasValue)
            {
                try
                {
                    // Cleanup uses only the remaining endpoint budget, independently of client disconnect.
                    if (completionStarted)
                    {
                        var committed = await store.ReadCompletionAsync(operationId.Value, deadline.Token);
                        if (committed is not null)
                        {
                            outcome = "idempotent_replay";
                            return new RegistrationResult(committed, true);
                        }
                        // Never delete a principal after an ambiguous local commit.
                        uncertain = true;
                    }
                    else if (principal is not null)
                    {
                        var current = await provider.FindUserAsync(principal.Username, deadline.Token);
                        if (current is not null && current.Subject != principal.Subject)
                            throw new RegistrationException(RegistrationFailure.ReconciliationRequired);
                        if (current is not null)
                            await provider.DeleteUserAsync(principal.Username, deadline.Token);
                        uncertain = false;
                    }
                    else if (exception is IdentityProviderException { OutcomeMayHaveChanged: false })
                    {
                        uncertain = false;
                    }
                }
                catch
                {
                    // The durable reservation remains the recovery anchor even when DB writes fail.
                    uncertain = true;
                }
                try
                {
                    await store.RecordFailureAsync(operationId.Value, failure, uncertain, clock.GetUtcNow(), deadline.Token);
                }
                catch
                {
                    uncertain = true;
                }
                if (uncertain)
                {
                    observer.ReconciliationRequired(operationId.Value, input.CorrelationId);
                    Reconciliations.Add(1);
                }
            }
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                outcome = "client_aborted";
                throw;
            }
            outcome = deadline.IsCancellationRequested ? "timeout" : "rejected";
            throw new RegistrationException(uncertain ? RegistrationFailure.ReconciliationRequired : failure);
        }
        finally
        {
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
            if (outcome is not ("success" or "idempotent_replay")) activity?.SetStatus(ActivityStatusCode.Error);
        }
    }

    public static string Validate(RegisterUserInput input)
    {
        var email = input.Email?.Trim().ToLowerInvariant();
        // Explicit ASCII address contract avoids database/provider Unicode normalization differences.
        if (string.IsNullOrEmpty(email) || email.Length > 128 || email.Any(c => c > 127 || char.IsWhiteSpace(c)) ||
            !MailAddress.TryCreate(email, out var address) || address.Address != email ||
            !address.Host.Contains('.') || input.Password is null || input.CorrelationId == Guid.Empty || input.IdempotencyKey == Guid.Empty)
            throw new RegistrationException(RegistrationFailure.InvalidInput);
        if (input.Password.Length is < 8 or > 256 ||
            !input.Password.Any(char.IsLetter) || !input.Password.Any(char.IsAsciiDigit) ||
            input.Password.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new RegistrationException(RegistrationFailure.PasswordPolicy);
        return email;
    }

    private static RegistrationFailure Classify(Exception exception) => exception switch
    {
        RegistrationException registration => registration.Failure,
        IdentityProviderException { Failure: IdentityProviderFailure.UserExists } => RegistrationFailure.UserExists,
        IdentityProviderException { Failure: IdentityProviderFailure.PasswordPolicy } => RegistrationFailure.PasswordPolicy,
        IdentityProviderException { Failure: IdentityProviderFailure.Unavailable } => RegistrationFailure.Unavailable,
        OperationCanceledException or TimeoutException => RegistrationFailure.Unavailable,
        System.Data.Common.DbException { IsTransient: true } => RegistrationFailure.Unavailable,
        _ when exception.InnerException is System.Data.Common.DbException { IsTransient: true } => RegistrationFailure.Unavailable,
        _ => RegistrationFailure.Unexpected
    };
}
