using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Expenses.Domain.Dto.Request;

public sealed class GetUsersRequest : IValidatableObject
{
    /// <summary>Número da página, começando em 1.</summary>
    [DefaultValue(1)]
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    /// <summary>Quantidade de itens por página, entre 1 e 100. Padrão: 20.</summary>
    [DefaultValue(20)]
    [Range(1, 100)]
    public int PageSize { get; init; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (((long)Page - 1) * PageSize > int.MaxValue)
        {
            yield return new ValidationResult(
                "The requested page exceeds the supported offset.", [nameof(Page)]);
        }
    }
}
