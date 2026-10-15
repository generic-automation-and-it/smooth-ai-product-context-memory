using FluentValidation;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>
/// The product-name identity rules shared by every registry/entry validator: exact, ≤ 200, no leading
/// or trailing whitespace, no control characters. Case is significant for identity but case-folded
/// collisions are refused by <c>UpsertProductName</c>.
/// </summary>
internal static class ProductNameValidation
{
    public static IValidator<string> NameValidator { get; } = new InlineValidator<string>
    {
        v => v.RuleFor(n => n)
            .NotEmpty()
            .MaximumLength(200)
            .Must(n => n.Trim() == n)
            .WithMessage("Name must not have leading or trailing whitespace.")
            .Must(n => !n.Any(char.IsControl))
            .WithMessage("Name must not contain control characters."),
    };
}
