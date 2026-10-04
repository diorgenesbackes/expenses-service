namespace Expenses.Domain.Dto.Response;

/// <summary>Dados públicos do perfil interno do usuário.</summary>
/// <param name="Id">Identificador UUID do usuário.</param>
/// <param name="Email">E-mail do perfil.</param>
/// <param name="CreatedAt">Instante de criação em UTC.</param>
/// <param name="UpdatedAt">Instante da última atualização em UTC.</param>
public sealed record UserResponse(
    Guid Id, string Email, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
