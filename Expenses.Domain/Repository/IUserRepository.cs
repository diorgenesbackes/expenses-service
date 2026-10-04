using Expenses.Domain.Entity;

namespace Expenses.Domain.Repository;

public interface IUserRepository
{
    Task<long> CountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<User>> ListAsync(int offset, int pageSize, CancellationToken cancellationToken);
}
