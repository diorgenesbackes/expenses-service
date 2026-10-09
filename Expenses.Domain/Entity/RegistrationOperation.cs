namespace Expenses.Domain.Entity;

public sealed class RegistrationOperation
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string NormalizedEmail { get; set; } = "";
    public Guid IdempotencyRecordId { get; set; }
    public Guid CorrelationId { get; set; }
    public string Stage { get; set; } = "reserved";
    public string? CognitoUsername { get; set; }
    public string? CognitoSub { get; set; }
    public Guid? UserId { get; set; }
    public long Version { get; set; }
}
