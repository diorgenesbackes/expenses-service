using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Expenses.Api.Configuration;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Expenses.Test;

public sealed class CognitoIdentityProviderTests
{
    private const string SensitiveValue = "synthetic-secret-never-log";
    private static CognitoSettings Settings => new()
    {
        Region = "us-east-1", UserPoolId = "us-east-1_test", ClientId = "testclient", ClientSecret = SensitiveValue
    };

    [Fact]
    public async Task Creation_suppresses_messages_and_does_not_claim_email_verification()
    {
        using var client = new StubCognitoClient();
        var provider = Provider(client);
        var user = await provider.CreateUserAsync("person@example.invalid", "Example123", default);
        Assert.Equal("subject-123", user.Subject);
        var request = Assert.IsType<AdminCreateUserRequest>(client.LastRequest);
        Assert.Equal(MessageActionType.SUPPRESS, request.MessageAction);
        Assert.False(request.ForceAliasCreation);
        Assert.Single(request.UserAttributes);
        Assert.DoesNotContain(request.UserAttributes, attribute => attribute.Name == "email_verified");
        Assert.Equal(0, client.PasswordCalls);

        await provider.SetPermanentPasswordAsync(user.Username, "Example123", default);
        var passwordRequest = Assert.IsType<AdminSetUserPasswordRequest>(client.LastRequest);
        Assert.True(passwordRequest.Permanent);
        Assert.Equal(user.Username, passwordRequest.Username);
    }

    [Fact]
    public async Task Login_uses_admin_flow_and_client_secret_hash_without_returning_id_token()
    {
        using var client = new StubCognitoClient();
        var tokens = await Provider(client).AuthenticateAsync("person@example.invalid", "Example123", default);
        var request = Assert.IsType<AdminInitiateAuthRequest>(client.LastRequest);
        Assert.Equal(AuthFlowType.ADMIN_USER_PASSWORD_AUTH, request.AuthFlow);
        var expectedHash = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SensitiveValue),
            Encoding.UTF8.GetBytes("person@example.invalidtestclient")));
        Assert.True(request.AuthParameters["SECRET_HASH"] == expectedHash);
        Assert.Equal(900, tokens.ExpiresInSeconds);
        Assert.DoesNotContain(SensitiveValue, tokens.ToString());
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(tokens));
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(Settings));
    }

    [Fact]
    public async Task Refresh_requires_rotation_and_uses_refresh_operation()
    {
        using var client = new StubCognitoClient();
        await Provider(client).RefreshAsync("original", default);
        var request = Assert.IsType<GetTokensFromRefreshTokenRequest>(client.LastRequest);
        Assert.True(request.ClientSecret == SensitiveValue);
        Assert.Equal("original", request.RefreshToken);
        client.AuthResult = new AuthenticationResultType { AccessToken = "a", ExpiresIn = 900, TokenType = "Bearer" };
        var error = await Assert.ThrowsAsync<IdentityProviderException>(() => Provider(client).RefreshAsync("original", default));
        Assert.Equal(IdentityProviderFailure.InvalidResponse, error.Failure);
        Assert.True(error.OutcomeMayHaveChanged);
    }

    [Fact]
    public async Task Challenges_do_not_create_a_successful_session()
    {
        using var client = new StubCognitoClient { Challenge = ChallengeNameType.NEW_PASSWORD_REQUIRED };
        var error = await Assert.ThrowsAsync<IdentityProviderException>(() =>
            Provider(client).AuthenticateAsync("person@example.invalid", "Example123", default));
        Assert.Equal(IdentityProviderFailure.ChallengeRequired, error.Failure);
    }

    [Theory]
    [InlineData("NotAuthorizedException", IdentityProviderFailure.InvalidCredentials)]
    [InlineData("UserNotFoundException", IdentityProviderFailure.InvalidCredentials)]
    [InlineData("TooManyRequestsException", IdentityProviderFailure.Unavailable)]
    [InlineData("InternalErrorException", IdentityProviderFailure.Unavailable)]
    [InlineData("AccessDeniedException", IdentityProviderFailure.Configuration)]
    public async Task Provider_errors_are_sanitized_and_not_retried(string code, IdentityProviderFailure expected)
    {
        using var client = new StubCognitoClient
        {
            Failure = new AmazonCognitoIdentityProviderException(SensitiveValue) { ErrorCode = code }
        };
        var error = await Assert.ThrowsAsync<IdentityProviderException>(() =>
            Provider(client).AuthenticateAsync("person@example.invalid", "Example123", default));
        Assert.Equal(expected, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SensitiveValue, error.ToString());
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Duplicate_creation_never_resets_password_or_deletes_existing_account()
    {
        using var client = new StubCognitoClient
        {
            Failure = new AmazonCognitoIdentityProviderException(SensitiveValue) { ErrorCode = "UsernameExistsException" }
        };
        var error = await Assert.ThrowsAsync<IdentityProviderException>(() =>
            Provider(client).CreateUserAsync("person@example.invalid", "Example123", default));
        Assert.Equal(IdentityProviderFailure.UserExists, error.Failure);
        Assert.False(error.OutcomeMayHaveChanged);
        Assert.Equal(0, client.PasswordCalls);
        Assert.Equal(0, client.DeleteCalls);
    }

    [Fact]
    public async Task Missing_users_are_absent_and_delete_is_idempotent()
    {
        using var client = new StubCognitoClient
        {
            Failure = new AmazonCognitoIdentityProviderException(SensitiveValue) { ErrorCode = "UserNotFoundException" }
        };
        Assert.Null(await Provider(client).FindUserAsync("immutable-username", default));
        await Provider(client).DeleteUserAsync("immutable-username", default);
    }

    [Fact]
    public async Task Transport_failure_marks_mutation_outcome_as_uncertain()
    {
        using var client = new StubCognitoClient { Failure = new HttpRequestException(SensitiveValue) };
        var error = await Assert.ThrowsAsync<IdentityProviderException>(() =>
            Provider(client).CreateUserAsync("person@example.invalid", "Example123", default));
        Assert.Equal(IdentityProviderFailure.Unavailable, error.Failure);
        Assert.True(error.OutcomeMayHaveChanged);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var client = new StubCognitoClient { WaitForCancellation = true };
        using var cancellation = new CancellationTokenSource();
        var request = Provider(client).AuthenticateAsync("person@example.invalid", "Example123", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Three_second_deadline_is_enforced_without_real_waits()
    {
        using var client = new StubCognitoClient { WaitForCancellation = true };
        var clock = new DeadlineClock();
        var provider = new CognitoIdentityProvider(client, Settings, clock);
        var request = provider.AuthenticateAsync("person@example.invalid", "Example123", default);
        Assert.Equal(TimeSpan.FromSeconds(3), clock.DueTime);
        clock.Expire();
        var error = await Assert.ThrowsAsync<IdentityProviderException>(() => request);
        Assert.Equal(IdentityProviderFailure.Unavailable, error.Failure);
    }

    [Fact]
    public void Missing_secret_is_rejected_only_when_provider_is_resolved()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cognito:Region"] = "us-east-1", ["Cognito:UserPoolId"] = "us-east-1_test",
            ["Cognito:ClientId"] = "testclient"
        }).Build();
        using var services = new ServiceCollection().AddIdentityProvider(configuration).BuildServiceProvider();
        var error = Assert.Throws<IdentityProviderException>(() => services.GetRequiredService<IIdentityProvider>());
        Assert.Equal(IdentityProviderFailure.Configuration, error.Failure);
    }

    private static CognitoIdentityProvider Provider(StubCognitoClient client) => new(client, Settings, TimeProvider.System);

    private sealed class StubCognitoClient() : AmazonCognitoIdentityProviderClient(
        new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public object? LastRequest { get; private set; }
        public Exception? Failure { get; init; }
        public bool WaitForCancellation { get; init; }
        public int Calls { get; private set; }
        public int PasswordCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public ChallengeNameType? Challenge { get; init; }
        public AuthenticationResultType AuthResult { get; set; } = new()
        {
            AccessToken = SensitiveValue + "-access", RefreshToken = SensitiveValue + "-refresh",
            IdToken = SensitiveValue + "-id", ExpiresIn = 900, TokenType = "Bearer"
        };

        private async Task<T> RespondAsync<T>(object request, T response, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Calls++;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Failure is not null) throw Failure;
            return response;
        }

        public override Task<AdminCreateUserResponse> AdminCreateUserAsync(AdminCreateUserRequest request,
            CancellationToken cancellationToken = default) => RespondAsync(request, new AdminCreateUserResponse
        {
            User = new UserType
            {
                Username = "immutable-username", Attributes =
                [new AttributeType { Name = "sub", Value = "subject-123" },
                 new AttributeType { Name = "email", Value = "person@example.invalid" }]
            }
        }, cancellationToken);

        public override Task<AdminSetUserPasswordResponse> AdminSetUserPasswordAsync(AdminSetUserPasswordRequest request,
            CancellationToken cancellationToken = default)
        {
            PasswordCalls++;
            return RespondAsync(request, new AdminSetUserPasswordResponse(), cancellationToken);
        }

        public override Task<AdminInitiateAuthResponse> AdminInitiateAuthAsync(AdminInitiateAuthRequest request,
            CancellationToken cancellationToken = default) => RespondAsync(request, new AdminInitiateAuthResponse
        { AuthenticationResult = AuthResult, ChallengeName = Challenge }, cancellationToken);

        public override Task<GetTokensFromRefreshTokenResponse> GetTokensFromRefreshTokenAsync(GetTokensFromRefreshTokenRequest request,
            CancellationToken cancellationToken = default) => RespondAsync(request,
                new GetTokensFromRefreshTokenResponse { AuthenticationResult = AuthResult }, cancellationToken);

        public override Task<AdminGetUserResponse> AdminGetUserAsync(AdminGetUserRequest request,
            CancellationToken cancellationToken = default) => RespondAsync(request, new AdminGetUserResponse(), cancellationToken);

        public override Task<AdminDeleteUserResponse> AdminDeleteUserAsync(AdminDeleteUserRequest request,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return RespondAsync(request, new AdminDeleteUserResponse(), cancellationToken);
        }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            DueTime = dueTime;
            return new NoOpTimer();
        }

        public void Expire() => callback?.Invoke(state);

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
