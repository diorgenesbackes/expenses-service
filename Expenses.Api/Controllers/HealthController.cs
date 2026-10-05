using Expenses.Api.HealthChecks;
using Expenses.Domain.Dto.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Expenses.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    /// <summary>Verifica se a API está respondendo.</summary>
    /// <remarks>Retorna o estado da aplicação sem consultar o banco de dados.</remarks>
    /// <response code="200">API disponível, com status "up".</response>
    [HttpGet(Name = "GetHealth")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK, "application/json")]
    public ActionResult<HealthResponse> Get()
    {
        return Ok(new HealthResponse("up"));
    }

    /// <summary>Verifica se a aplicação consegue acessar o banco de dados.</summary>
    /// <remarks>
    /// Usa a conexão configurada para Expenses, com limite de cinco segundos.
    /// Não consulta tabelas de negócio nem valida a estrutura do banco.
    /// Disponível em todos os ambientes, sem armazenar o resultado em cache.
    /// </remarks>
    /// <param name="healthChecks">Executor das verificações de disponibilidade.</param>
    /// <param name="cancellationToken">Cancelamento da requisição.</param>
    /// <response code="200">Banco acessível, com status "up".</response>
    /// <response code="503">Banco inacessível, configuração inválida ou timeout, com status "down".</response>
    [HttpGet("db", Name = "GetDatabaseHealth")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable, "application/json")]
    public async Task<ActionResult<HealthResponse>> GetDatabase(
        [FromServices] HealthCheckService healthChecks, CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Name == DatabaseHealthCheck.Name, cancellationToken);
        var available = report.Status == HealthStatus.Healthy;

        return StatusCode(available ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable,
            new HealthResponse(available ? "up" : "down"));
    }
}
