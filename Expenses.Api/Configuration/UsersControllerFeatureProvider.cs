using System.Reflection;
using Expenses.Api.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Expenses.Api.Configuration;

internal sealed class UsersControllerFeatureProvider(bool isDevelopment)
    : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        // This diagnostic lists global user profiles and has no production authorization policy yet.
        if (!isDevelopment)
        {
            feature.Controllers.Remove(typeof(UsersController).GetTypeInfo());
        }
    }
}
