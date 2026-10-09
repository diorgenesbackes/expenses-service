using Amazon.CognitoIdentityProvider;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Expenses.Api.Configuration;

public static class IdentityRegistration
{
    public static IServiceCollection AddIdentityProvider(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolve only when used: existing health/database diagnostics must not depend on AWS credentials.
        services.AddSingleton(_ =>
        {
            var settings = new CognitoSettings
            {
                Region = configuration["Cognito:Region"] ?? "",
                UserPoolId = configuration["Cognito:UserPoolId"] ?? "",
                ClientId = configuration["Cognito:ClientId"] ?? "",
                ClientSecret = configuration["Cognito:ClientSecret"] ?? ""
            };
            settings.Validate();
            return settings;
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAmazonCognitoIdentityProvider>(provider =>
            CognitoClientFactory.Create(provider.GetRequiredService<CognitoSettings>(), configuration["AWS:Profile"]));
        services.AddSingleton<IIdentityProvider, CognitoIdentityProvider>();
        return services;
    }
}
