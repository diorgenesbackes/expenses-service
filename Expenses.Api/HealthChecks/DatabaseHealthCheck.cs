using Expenses.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Expenses.Api.HealthChecks;

internal sealed class DatabaseHealthCheck(
    IServiceProvider services,
    ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    public const string Name = "database";
    public const int TimeoutSeconds = 5;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // The health-check runner supplies a scope. Resolve here so missing or
            // invalid connection configuration also reports an unhealthy database.
            var dbContext = services.GetRequiredService<ExpensesDbContext>();

            // Npgsql's authentication handshake can wait for the connection timeout
            // even after cancellation. Cap it on this check's scoped context only.
            var connection = new NpgsqlConnectionStringBuilder(dbContext.Database.GetConnectionString());
            if (connection.Timeout == 0 || connection.Timeout > TimeoutSeconds)
            {
                connection.Timeout = TimeoutSeconds;
                dbContext.Database.SetConnectionString(connection.ConnectionString);
            }

            var connected = await dbContext.Database.CanConnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return connected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never expose connection details in the health report or logs.
            logger.LogWarning("Database health check failed with {ExceptionType}", exception.GetType().Name);
            return HealthCheckResult.Unhealthy();
        }
    }
}
