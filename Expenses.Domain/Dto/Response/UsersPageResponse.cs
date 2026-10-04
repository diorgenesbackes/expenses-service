namespace Expenses.Domain.Dto.Response;

/// <summary>Página de usuários ordenados por ID crescente.</summary>
/// <param name="Items">Usuários da página; coleção vazia quando não há resultados.</param>
/// <param name="Page">Número da página consultada.</param>
/// <param name="PageSize">Tamanho solicitado para a página.</param>
/// <param name="TotalCount">Quantidade total de usuários, independentemente da página.</param>
public sealed record UsersPageResponse(
    IReadOnlyList<UserResponse> Items, int Page, int PageSize, long TotalCount);
