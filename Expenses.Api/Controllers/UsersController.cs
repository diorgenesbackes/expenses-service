using Expenses.Application.Service;
using Expenses.Domain.Dto.Request;
using Expenses.Domain.Dto.Response;
using Microsoft.AspNetCore.Mvc;

namespace Expenses.Api.Controllers;

[ApiController]
[Route("v1/identity/users")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class UsersController(IUserService service) : ControllerBase
{
    /// <summary>Lista os usuários com paginação.</summary>
    /// <remarks>
    /// Disponível somente em Development. Ordenação por ID crescente.
    /// Página padrão: 1; tamanho padrão: 20; máximo: 100 itens.
    /// Uma página sem resultados retorna 200 com items vazio.
    /// O offset calculado deve caber em um inteiro de 32 bits.
    /// </remarks>
    /// <param name="request">Parâmetros de paginação.</param>
    /// <param name="cancellationToken">Cancelamento da requisição.</param>
    /// <response code="200">Página de usuários e quantidade total de registros.</response>
    /// <response code="400">Parâmetros de paginação inválidos.</response>
    /// <response code="503">Banco temporariamente indisponível ou tempo de consulta excedido.</response>
    /// <response code="500">Falha inesperada na consulta.</response>
    [HttpGet(Name = "GetUsers")]
    [ProducesResponseType<UsersPageResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<ActionResult<UsersPageResponse>> Get(
        [FromQuery] GetUsersRequest request, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(request, cancellationToken));
}
