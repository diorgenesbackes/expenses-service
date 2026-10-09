using Expenses.Domain.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Expenses.Infrastructure.Persistence;

internal sealed class RegistrationOperationConfiguration : IEntityTypeConfiguration<RegistrationOperation>
{
    public void Configure(EntityTypeBuilder<RegistrationOperation> builder)
    {
        builder.ToTable("registration_operations", "identity");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(320).IsRequired();
        builder.Property(x => x.IdempotencyRecordId).HasColumnName("idempotency_record_id");
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        builder.Property(x => x.Stage).HasColumnName("stage").HasMaxLength(32).IsRequired();
        builder.Property(x => x.CognitoUsername).HasColumnName("cognito_username").HasMaxLength(128);
        builder.Property(x => x.CognitoSub).HasColumnName("cognito_sub").HasMaxLength(128);
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
    }
}
