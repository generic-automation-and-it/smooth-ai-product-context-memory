using FluentValidation;

namespace SmoothAiProductContextMemory.Application.Common.Models;

/// <summary>
/// The ticket rules both group writers need. Shared because a ticket that is legal to create a group
/// with must be legal to merge into one — two copies of these rules drift apart silently.
/// </summary>
/// <remarks>
/// <see cref="TicketInput.Url"/> is non-nullable in the contract, but an omitted JSON member binds to
/// null through the record constructor. That null reaches jsonb and the export renderer dereferences
/// it, so it is rejected here rather than persisted. An empty url is legitimate; a missing one is not.
/// </remarks>
public sealed class TicketInputValidator : AbstractValidator<TicketInput>
{
    public TicketInputValidator()
    {
        RuleFor(t => t.Provider).NotEmpty();
        RuleFor(t => t.Key).NotEmpty();
        RuleFor(t => t.Url).NotNull();
    }
}
