using Expenses.Domain.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Expenses.Infrastructure.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // Liquibase owns the schema; this mapping never creates or migrates tables.
        builder.ToTable("users", "identity");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(user => user.CognitoSub).HasColumnName("cognito_sub").HasMaxLength(128).IsRequired();
        builder.Property(user => user.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(user => user.NormalizedEmail)
            .HasColumnName("normalized_email").HasMaxLength(320)
            .HasComputedColumnSql("lower(btrim(email))", stored: true);
        builder.Property(user => user.CreatedAt)
            .HasColumnName("created_at").HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(user => user.UpdatedAt)
            .HasColumnName("updated_at").HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAddOrUpdate();
        builder.HasIndex(user => user.CognitoSub).IsUnique();
        builder.HasIndex(user => user.NormalizedEmail).IsUnique();
    }
}
