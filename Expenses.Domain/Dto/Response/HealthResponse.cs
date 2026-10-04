namespace Expenses.Domain.Dto.Response;

/// <summary>Estado de disponibilidade da API.</summary>
/// <param name="Status">Valor "up" quando a API está respondendo.</param>
public sealed record HealthResponse(string Status);
