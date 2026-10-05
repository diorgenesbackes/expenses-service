namespace Expenses.Domain.Dto.Response;

/// <summary>Estado de disponibilidade do recurso verificado.</summary>
/// <param name="Status">Valor "up" quando disponível ou "down" quando indisponível.</param>
public sealed record HealthResponse(string Status);
