using Expenses.Domain.Dto.Response;
using Microsoft.AspNetCore.Mvc;

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
}
