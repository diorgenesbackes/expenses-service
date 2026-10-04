using Expenses.Domain.Entity;
using Expenses.Domain.Repository;

namespace Expenses.Test.Support;

internal sealed class StubUserRepository : IUserRepository
{
    public Func<CancellationToken, Task<long>> Count { get; init; } = _ => Task.FromResult(0L);
    public Func<int, int, CancellationToken, Task<IReadOnlyList<User>>> List { get; init; } =
        (_, _, _) => Task.FromResult<IReadOnlyList<User>>([]);
    public int Calls { get; private set; }

    public Task<long> CountAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Count(cancellationToken);
    }

    public Task<IReadOnlyList<User>> ListAsync(int offset, int pageSize, CancellationToken cancellationToken)
    {
        Calls++;
        return List(offset, pageSize, cancellationToken);
    }
}
