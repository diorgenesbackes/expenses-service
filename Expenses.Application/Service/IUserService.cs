using Expenses.Domain.Dto.Request;
using Expenses.Domain.Dto.Response;

namespace Expenses.Application.Service;

public interface IUserService
{
    Task<UsersPageResponse> GetAsync(GetUsersRequest request, CancellationToken cancellationToken);
}
