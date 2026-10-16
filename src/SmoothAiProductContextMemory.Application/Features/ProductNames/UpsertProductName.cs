using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>
/// Creates a product, or updates the supplied fields of an existing one. Keyed by name, like
/// <c>UpsertInitiative</c>. A case-folded duplicate name is refused; an unknown product is never
/// created implicitly.
/// </summary>
public static class UpsertProductName
{
    public sealed record Request(string Name, string? Description = null, string? Status = null) : IRequest<Response>;

    public sealed record Response(string Name, string? Description, string Status, bool Created);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Name).SetValidator(ProductNameValidation.NameValidator);
            RuleFor(x => x.Description).MaximumLength(1000).Must(d => d is null || !d.Contains('\0'))
                .WithMessage("Description must not contain a null character.");
            RuleFor(x => x.Status)
                .Must(s => s is null
                    || s == Initiative.InitiativeStatus.Active
                    || s == Initiative.InitiativeStatus.Archived)
                .WithMessage("Status must be active or archived.");
        }
    }

    public sealed class Handler(
        IProductGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Upsert product name started");

            bool created = await graph.UpsertAsync(
                request.Name, request.Description, request.Status, cancellationToken);
            ProductRegistryRow? row = await graph.GetAsync(request.Name, cancellationToken);

            logger.LogInformation("Upsert product name completed. Created: {Created}", created);
            return new Response(
                row?.Name ?? request.Name,
                row?.Description,
                row?.Status ?? Initiative.InitiativeStatus.Active,
                created);
        }
    }
}
