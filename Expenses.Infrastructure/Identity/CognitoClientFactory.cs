using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Expenses.Application.Identity;

namespace Expenses.Infrastructure.Identity;

public static class CognitoClientFactory
{
    public static IAmazonCognitoIdentityProvider Create(CognitoSettings settings, string? profile)
    {
        settings.Validate();
        var config = new AmazonCognitoIdentityProviderConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region),
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(3),
            LogResponse = false,
            LogMetrics = false
        };

        try
        {
            if (string.IsNullOrWhiteSpace(profile))
            {
                // Deployed workloads use the standard SDK credential chain, including IAM roles.
                return new AmazonCognitoIdentityProviderClient(config);
            }

            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile, out var credentials))
            {
                throw new IdentityProviderException(IdentityProviderFailure.Configuration);
            }

            return new AmazonCognitoIdentityProviderClient(credentials, config);
        }
        catch (AmazonClientException)
        {
            throw new IdentityProviderException(IdentityProviderFailure.Configuration);
        }
    }
}
