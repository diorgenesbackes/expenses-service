using Expenses.Application.Identity;

namespace Expenses.Api.Configuration;

internal sealed class RegistrationObserver(ILogger<RegistrationObserver> logger) : IRegistrationObserver
{
    public void ReconciliationRequired(Guid operationId, Guid correlationId) =>
        logger.LogError(new EventId(3100, "identity.registration.reconciliation_required"),
            "Registration requires reconciliation; {operationId}; {correlationId}; {module}; {operation}",
            operationId, correlationId, "identity", "identity.users.create");
}
