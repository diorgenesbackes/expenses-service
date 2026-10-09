using Expenses.Application.Identity;
using Expenses.Test.Support;

namespace Expenses.Test;

public sealed class RegistrationServiceTests
{
    [Theory]
    [InlineData("invalid", "Example123", RegistrationFailure.InvalidInput)]
    [InlineData("first.last@localhost", "Example123", RegistrationFailure.InvalidInput)]
    [InlineData("display <a@example.test>", "Example123", RegistrationFailure.InvalidInput)]
    [InlineData("a@example.test", "short1", RegistrationFailure.PasswordPolicy)]
    [InlineData("a@example.test", "onlyletters", RegistrationFailure.PasswordPolicy)]
    [InlineData("a@example.test", "12345678", RegistrationFailure.PasswordPolicy)]
    [InlineData("a@example.test", "Example 123", RegistrationFailure.PasswordPolicy)]
    public async Task Invalid_input_has_no_external_or_persistent_effect(string email, string password, RegistrationFailure expected)
    {
        var store = new StubRegistrationStore();
        var provider = new StubIdentityProvider();
        var error = await Assert.ThrowsAsync<RegistrationException>(() => Service(store, provider).RegisterAsync(Input(email, password), default));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(0, provider.Creates);
        Assert.Equal(0, store.Reservations);
    }

    [Fact]
    public async Task Success_normalizes_email_and_finishes_before_returning()
    {
        var store = new StubRegistrationStore();
        var provider = new StubIdentityProvider();
        var result = await Service(store, provider).RegisterAsync(Input(" PERSON@EXAMPLE.TEST "), default);
        Assert.Equal("person@example.test", provider.LastEmail);
        Assert.Equal(store.User.Id, result.User.Id);
        Assert.Equal(1, provider.PasswordChanges);
        Assert.Equal(0, provider.Deletes);
    }

    [Fact]
    public async Task Replay_does_not_change_password_or_create_another_principal()
    {
        var store = new StubRegistrationStore();
        store.Replay = new RegistrationResult(store.User, true);
        var provider = new StubIdentityProvider();
        var result = await Service(store, provider).RegisterAsync(Input(password: "Different123"), default);
        Assert.True(result.Replayed);
        Assert.Equal(0, provider.Creates);
        Assert.Equal(0, provider.PasswordChanges);
    }

    [Fact]
    public async Task Definite_duplicate_does_not_delete_or_reset_a_preexisting_account()
    {
        var provider = new StubIdentityProvider { CreateFailure = new IdentityProviderException(IdentityProviderFailure.UserExists) };
        var store = new StubRegistrationStore();
        var error = await Assert.ThrowsAsync<RegistrationException>(() => Service(store, provider).RegisterAsync(Input(), default));
        Assert.Equal(RegistrationFailure.UserExists, error.Failure);
        Assert.False(store.Uncertain);
        Assert.Equal(0, provider.Deletes);
        Assert.Equal(0, provider.PasswordChanges);
    }

    [Fact]
    public async Task Unknown_creation_never_guesses_ownership_from_email()
    {
        var provider = new StubIdentityProvider { CreateFailure = new IdentityProviderException(IdentityProviderFailure.Unavailable, true) };
        var store = new StubRegistrationStore();
        var observer = new StubRegistrationObserver();
        var error = await Assert.ThrowsAsync<RegistrationException>(() => Service(store, provider, observer).RegisterAsync(Input(), default));
        Assert.Equal(RegistrationFailure.ReconciliationRequired, error.Failure);
        Assert.True(store.Uncertain);
        Assert.Equal(0, provider.Deletes);
        Assert.Equal(1, observer.Signals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_password_or_principal_write_compensates_only_created_principal(bool persistenceFailure)
    {
        var provider = new StubIdentityProvider();
        var store = new StubRegistrationStore();
        if (persistenceFailure) store.PrincipalFailure = new IOException("sensitive-data");
        else provider.PasswordFailure = new IdentityProviderException(IdentityProviderFailure.PasswordPolicy);
        var error = await Assert.ThrowsAsync<RegistrationException>(() => Service(store, provider).RegisterAsync(Input(), default));
        Assert.Equal(1, provider.Deletes);
        Assert.False(store.Uncertain);
        Assert.DoesNotContain("sensitive-data", error.ToString());
    }

    [Fact]
    public async Task Failed_compensation_keeps_reconciliation_required()
    {
        var provider = new StubIdentityProvider
        {
            PasswordFailure = new IdentityProviderException(IdentityProviderFailure.PasswordPolicy),
            DeleteFailure = new IdentityProviderException(IdentityProviderFailure.Unavailable, true)
        };
        var observer = new StubRegistrationObserver();
        await Assert.ThrowsAsync<RegistrationException>(() => Service(new StubRegistrationStore(), provider, observer).RegisterAsync(Input(), default));
        Assert.Equal(1, observer.Signals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_commit_is_checked_and_never_compensated(bool committed)
    {
        var store = new StubRegistrationStore { CompleteFailure = new IOException("private"), CommittedDespiteFailure = committed };
        var provider = new StubIdentityProvider();
        if (committed)
        {
            var result = await Service(store, provider).RegisterAsync(Input(), default);
            Assert.Equal(store.User.Id, result.User.Id);
        }
        else await Assert.ThrowsAsync<RegistrationException>(() => Service(store, provider).RegisterAsync(Input(), default));
        Assert.Equal(0, provider.Deletes);
    }

    private static RegistrationService Service(StubRegistrationStore store, StubIdentityProvider provider, StubRegistrationObserver? observer = null) =>
        new(store, provider, TimeProvider.System, observer ?? new StubRegistrationObserver());
    private static RegisterUserInput Input(string email = "person@example.test", string password = "Example123") =>
        new(email, password, Guid.NewGuid(), Guid.NewGuid());
}
