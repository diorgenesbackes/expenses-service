using System.ComponentModel.DataAnnotations;
using Expenses.Domain.Dto.Request;
using Expenses.Domain.Dto.Response;
using Expenses.Domain.Mapper;
using Expenses.Domain.Repository;

namespace Expenses.Application.Service;

public sealed class UserService(IUserRepository repository) : IUserService
{
    public async Task<UsersPageResponse> GetAsync(GetUsersRequest request, CancellationToken cancellationToken)
    {
        // Keep the contract valid for callers outside MVC as well.
        Validator.ValidateObject(request, new ValidationContext(request), validateAllProperties: true);
        var offset = checked((request.Page - 1) * request.PageSize);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var totalCount = await repository.CountAsync(timeout.Token);
            var users = await repository.ListAsync(offset, request.PageSize, timeout.Token);
            return new UsersPageResponse(
                users.Select(UserMapper.ToResponse).ToArray(), request.Page, request.PageSize, totalCount);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The users query exceeded its time budget.", exception);
        }
    }
}
