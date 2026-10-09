using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Expenses.Application.Identity;

namespace Expenses.Infrastructure.Identity;

public sealed class CognitoIdentityProvider : IIdentityProvider
{
    public const string InstrumentationName = "Expenses.Identity.Cognito";
    private static readonly ActivitySource Activities = new(InstrumentationName);
    private static readonly Meter Metrics = new(InstrumentationName);
    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>(
        "expenses.identity.cognito.operation.duration", "s");
    private static readonly Counter<long> Errors = Metrics.CreateCounter<long>(
        "expenses.identity.cognito.operation.errors");
    private readonly IAmazonCognitoIdentityProvider client;
    private readonly CognitoSettings settings;
    private readonly TimeProvider timeProvider;

    public CognitoIdentityProvider(IAmazonCognitoIdentityProvider client, CognitoSettings settings,
        TimeProvider timeProvider)
    {
        settings.Validate();
        this.client = client;
        this.settings = settings;
        this.timeProvider = timeProvider;
    }

    public Task<ExternalIdentity> CreateUserAsync(string email, string temporaryPassword,
        CancellationToken cancellationToken) =>
        ExecuteAsync("create_user", true, async token =>
        {
            var response = await client.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = settings.UserPoolId,
                Username = email,
                TemporaryPassword = temporaryPassword,
                MessageAction = MessageActionType.SUPPRESS,
                ForceAliasCreation = false,
                UserAttributes = [new AttributeType { Name = "email", Value = email }]
            }, token);
            return MapUser(response.User?.Username, response.User?.Attributes, true);
        }, cancellationToken);

    public Task SetPermanentPasswordAsync(string username, string password,
        CancellationToken cancellationToken) =>
        ExecuteAsync("set_password", true, token => client.AdminSetUserPasswordAsync(
            new AdminSetUserPasswordRequest
            {
                UserPoolId = settings.UserPoolId,
                Username = username,
                Password = password,
                Permanent = true
            }, token), cancellationToken);

    public async Task<ExternalIdentity?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync("find_user", false, async token =>
            {
                var response = await client.AdminGetUserAsync(new AdminGetUserRequest
                {
                    UserPoolId = settings.UserPoolId,
                    Username = username
                }, token);
                return MapUser(response.Username, response.UserAttributes, false);
            }, cancellationToken);
        }
        catch (IdentityProviderException exception) when (exception.Failure == IdentityProviderFailure.UserNotFound)
        {
            return null;
        }
    }

    public Task<IdentityTokens> AuthenticateAsync(string email, string password,
        CancellationToken cancellationToken) =>
        ExecuteAsync("authenticate", true, async token =>
        {
            var response = await client.AdminInitiateAuthAsync(new AdminInitiateAuthRequest
            {
                UserPoolId = settings.UserPoolId,
                ClientId = settings.ClientId,
                AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                AuthParameters = new Dictionary<string, string>
                {
                    ["USERNAME"] = email,
                    ["PASSWORD"] = password,
                    ["SECRET_HASH"] = SecretHash(email)
                }
            }, token);
            if (!string.IsNullOrEmpty(response.ChallengeName?.Value))
            {
                // MFA and first-login password challenges are outside the agreed MVP.
                throw new IdentityProviderException(IdentityProviderFailure.ChallengeRequired);
            }
            return MapTokens(response.AuthenticationResult);
        }, cancellationToken);

    public Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        ExecuteAsync("refresh", true, async token =>
        {
            var response = await client.GetTokensFromRefreshTokenAsync(new GetTokensFromRefreshTokenRequest
            {
                ClientId = settings.ClientId,
                ClientSecret = settings.ClientSecret,
                RefreshToken = refreshToken
            }, token);
            // Rotation is required: silently reusing the old refresh token would hide misconfiguration.
            return MapTokens(response.AuthenticationResult);
        }, cancellationToken);

    public Task RevokeAsync(string refreshToken, CancellationToken cancellationToken) =>
        ExecuteAsync("revoke", true, token => client.RevokeTokenAsync(new RevokeTokenRequest
        {
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret,
            Token = refreshToken
        }, token), cancellationToken);

    public async Task DeleteUserAsync(string username, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync("delete_user", true, token => client.AdminDeleteUserAsync(new AdminDeleteUserRequest
            {
                UserPoolId = settings.UserPoolId,
                Username = username
            }, token), cancellationToken);
        }
        catch (IdentityProviderException exception) when (exception.Failure == IdentityProviderFailure.UserNotFound)
        {
            // A completed compensation remains successful when repeated.
        }
    }

    private string SecretHash(string username) => Convert.ToBase64String(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(settings.ClientSecret), Encoding.UTF8.GetBytes(username + settings.ClientId)));

    private static ExternalIdentity MapUser(string? username, List<AttributeType>? attributes, bool mutation)
    {
        var subject = attributes?.FirstOrDefault(attribute => attribute.Name == "sub")?.Value;
        var email = attributes?.FirstOrDefault(attribute => attribute.Name == "email")?.Value;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            throw new IdentityProviderException(IdentityProviderFailure.InvalidResponse, mutation);
        }
        return new ExternalIdentity(username, subject, email);
    }

    private static IdentityTokens MapTokens(AuthenticationResultType? result)
    {
        if (string.IsNullOrWhiteSpace(result?.AccessToken) || string.IsNullOrWhiteSpace(result.RefreshToken) ||
            result.ExpiresIn is not > 0 || !string.Equals(result.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new IdentityProviderException(IdentityProviderFailure.InvalidResponse, true);
        }
        return new IdentityTokens(result.AccessToken, result.RefreshToken, result.ExpiresIn.Value);
    }

    private async Task<T> ExecuteAsync<T>(string operation, bool mutation,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = Activities.StartActivity("cognito.operation", ActivityKind.Client);
        activity?.SetTag("operation", operation);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await action(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            // A cancelled mutation can have reached Cognito; the caller must reconcile its pending operation.
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "unavailable";
            throw new IdentityProviderException(IdentityProviderFailure.Unavailable, mutation);
        }
        catch (AmazonServiceException exception)
        {
            var failure = MapFailure(exception, operation);
            outcome = failure.ToString();
            throw new IdentityProviderException(failure, mutation && failure == IdentityProviderFailure.Unavailable);
        }
        catch (AmazonClientException)
        {
            outcome = "configuration";
            throw new IdentityProviderException(IdentityProviderFailure.Configuration, mutation);
        }
        catch (HttpRequestException)
        {
            outcome = "unavailable";
            throw new IdentityProviderException(IdentityProviderFailure.Unavailable, mutation);
        }
        catch (IdentityProviderException exception)
        {
            outcome = exception.Failure.ToString();
            throw;
        }
        finally
        {
            var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            if (outcome != "success")
            {
                Errors.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Error, outcome);
            }
        }
    }

    private static IdentityProviderFailure MapFailure(AmazonServiceException exception, string operation) =>
        exception.ErrorCode switch
        {
            "UsernameExistsException" or "AliasExistsException" => IdentityProviderFailure.UserExists,
            "InvalidPasswordException" or "PasswordHistoryPolicyViolationException" => IdentityProviderFailure.PasswordPolicy,
            "UserNotFoundException" when operation == "authenticate" => IdentityProviderFailure.InvalidCredentials,
            "UserNotFoundException" => IdentityProviderFailure.UserNotFound,
            "NotAuthorizedException" or "UserNotConfirmedException" or "PasswordResetRequiredException"
                when operation is "authenticate" or "refresh" => IdentityProviderFailure.InvalidCredentials,
            "TooManyRequestsException" or "InternalErrorException" or "LimitExceededException" => IdentityProviderFailure.Unavailable,
            _ when (int)exception.StatusCode >= 500 || exception.StatusCode == HttpStatusCode.RequestTimeout =>
                IdentityProviderFailure.Unavailable,
            _ => IdentityProviderFailure.Configuration
        };
}
