using System.Text.RegularExpressions;
using Expenses.Application.Identity;

namespace Expenses.Infrastructure.Identity;

/// <summary>Server-only configuration. Validation reports no configuration values.</summary>
public sealed class CognitoSettings
{
    public string Region { get; init; } = "";
    public string UserPoolId { get; init; } = "";
    public string ClientId { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ClientSecret { get; init; } = "";

    public void Validate()
    {
        if (!Regex.IsMatch(Region, @"\A[a-z]{2}(?:-[a-z]+)+-\d+\z") ||
            !UserPoolId.StartsWith(Region + "_", StringComparison.Ordinal) ||
            !Regex.IsMatch(UserPoolId, @"\A[\w-]+_[A-Za-z0-9]+\z") ||
            !Regex.IsMatch(ClientId, @"\A[\w]+\z") ||
            string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new IdentityProviderException(IdentityProviderFailure.Configuration);
        }
    }

    public override string ToString() => "CognitoSettings [redacted]";
}
