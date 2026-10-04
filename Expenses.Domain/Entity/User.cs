namespace Expenses.Domain.Entity;

public sealed class User
{
    public Guid Id { get; set; }
    public string CognitoSub { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? NormalizedEmail { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
