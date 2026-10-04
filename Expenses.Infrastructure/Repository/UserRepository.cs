using Expenses.Domain.Entity;
using Expenses.Domain.Repository;
using Expenses.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Expenses.Infrastructure.Repository;

public sealed class UserRepository(ExpensesDbContext context) : IUserRepository
{
    public Task<long> CountAsync(CancellationToken cancellationToken) =>
        context.Users.LongCountAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> ListAsync(int offset, int pageSize, CancellationToken cancellationToken) =>
        await context.Users.AsNoTracking()
            .OrderBy(user => user.Id)
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
}
