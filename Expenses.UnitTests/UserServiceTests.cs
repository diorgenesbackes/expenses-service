using System.ComponentModel.DataAnnotations;
using Expenses.Application.Service;
using Expenses.Domain.Dto.Request;
using Expenses.Domain.Entity;
using Expenses.Test.Support;

namespace Expenses.Test;

public sealed class UserServiceTests
{
    [Fact]
    public async Task Returns_page_metadata_and_maps_dates_to_utc()
    {
        var instant = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-3));
        var user = new User { Id = Guid.NewGuid(), Email = "user@example.test", CreatedAt = instant, UpdatedAt = instant };
        var repository = new StubUserRepository
        {
            Count = _ => Task.FromResult(5L),
            List = (offset, size, token) =>
            {
                Assert.Equal(4, offset);
                Assert.Equal(2, size);
                Assert.True(token.CanBeCanceled);
                return Task.FromResult<IReadOnlyList<User>>([user]);
            }
        };

        var result = await new UserService(repository).GetAsync(new GetUsersRequest { Page = 3, PageSize = 2 }, default);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(3, result.Page);
        Assert.Equal(2, result.PageSize);
        var item = Assert.Single(result.Items);
        Assert.Equal(user.Id, item.Id);
        Assert.Equal(user.Email, item.Email);
        Assert.Equal(instant.UtcDateTime, item.CreatedAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, item.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, item.UpdatedAt.Offset);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 2)]
    public async Task Rejects_invalid_pagination_before_querying(int page, int size)
    {
        var repository = new StubUserRepository();
        await Assert.ThrowsAsync<ValidationException>(() => new UserService(repository)
            .GetAsync(new GetUsersRequest { Page = page, PageSize = size }, default));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Preserves_request_cancellation()
    {
        var repository = WaitingRepository();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UserService(repository)
            .GetAsync(new GetUsersRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Converts_query_budget_expiration_to_timeout()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => new UserService(WaitingRepository())
            .GetAsync(new GetUsersRequest(), default).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    private static StubUserRepository WaitingRepository() => new()
    {
        Count = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    };
}
