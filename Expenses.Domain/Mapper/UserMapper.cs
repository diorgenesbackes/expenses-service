using Expenses.Domain.Dto.Response;
using Expenses.Domain.Entity;

namespace Expenses.Domain.Mapper;

public static class UserMapper
{
    public static UserResponse ToResponse(User user) =>
        new(user.Id, user.Email, user.CreatedAt.ToUniversalTime(), user.UpdatedAt.ToUniversalTime());
}
