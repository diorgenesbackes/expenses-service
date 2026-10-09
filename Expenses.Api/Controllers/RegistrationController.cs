using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Expenses.Application.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Expenses.Api.Controllers;

[ApiController]
[Route("v1/identity/users")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegistrationController(IRegistrationService service) : ControllerBase
{
    /// <summary>Cria conta no Cognito e perfil interno, sem iniciar sessão.</summary>
    /// <remarks>
    /// E-mail ASCII com até 128 caracteres, normalizado em minúsculas; senha de 8 a 256 caracteres,
    /// com letra e número ASCII, sem espaços ou controles. JSON limitado a 8 KB; propriedades desconhecidas são rejeitadas.
    /// Idempotency-Key é um UUID opcional, recomendado, com replay por 24 horas. Mesma chave/outro e-mail: 409.
    /// Trocar a senha em replay não modifica credenciais. Resultado incerto exige reconciliação, nunca nova criação automática.
    /// Operação anônima sem cookies de sessão; origens de navegador são validadas. Prazo total de processamento: 5 segundos.
    /// </remarks>
    [HttpPost(Name = "RegisterUser")]
    [Consumes("application/json")]
    [RequestSizeLimit(8192)]
    [ProducesResponseType<RegisterUserResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(413)]
    [ProducesResponseType<ProblemDetails>(415)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(500)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<ActionResult<RegisterUserResponse>> Post([FromBody] RegisterUserRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        Guid? key = null;
        if (Request.Headers.ContainsKey("Idempotency-Key"))
        {
            if (Request.Headers["Idempotency-Key"].Count != 1 || !Guid.TryParseExact(idempotencyKey, "D", out var parsed) || parsed == Guid.Empty)
                throw new RegistrationException(RegistrationFailure.InvalidInput);
            key = parsed;
        }
        var result = await service.RegisterAsync(new RegisterUserInput(request.Email, request.Password, key,
            (Guid)HttpContext.Items["correlationId"]!), cancellationToken);
        return StatusCode(201, new RegisterUserResponse(result.User));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegisterUserRequest
{
    [Required, MaxLength(320)]
    public string? Email { get; init; }
    [Required]
    public string? Password { get; init; }
}

public sealed record RegisterUserResponse(RegisteredUser User);
