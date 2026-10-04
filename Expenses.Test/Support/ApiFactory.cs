using Expenses.Domain.Repository;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Expenses.Test.Support;

internal sealed class ApiFactory(
    string environment = "Development",
    IUserRepository? repository = null,
    string? connectionString = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Logging:LogLevel:Default", "None");
        builder.UseSetting("ConnectionStrings:Expenses", connectionString ??
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        builder.ConfigureServices(services =>
        {
            if (repository is not null)
            {
                services.RemoveAll<IUserRepository>();
                services.AddSingleton(repository);
            }
        });
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
}
