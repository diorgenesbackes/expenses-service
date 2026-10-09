using Expenses.Domain.Entity;
using Microsoft.EntityFrameworkCore;

namespace Expenses.Infrastructure.Persistence;

public sealed class ExpensesDbContext(DbContextOptions<ExpensesDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RegistrationOperation> Registrations => Set<RegistrationOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new RegistrationOperationConfiguration());
    }
}
