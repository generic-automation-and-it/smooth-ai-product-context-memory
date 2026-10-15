using FluentValidation;
using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>
/// One-hop product links: <c>?productName=X</c> returns X's initiatives and repos; <c>?initiativeName=Y</c>
/// returns the products linked to Y. Exactly one query term is required; an unknown entity is a 404.
/// </summary>
public static class GetProductLinks
{
    public sealed record Request(string? ProductName = null, string? InitiativeName = null) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<ProductLinkItem> Items);

    public sealed record ProductLinkItem(string? ProductName, string? InitiativeName, string? Repo);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x)
                .Must(x => !(string.IsNullOrWhiteSpace(x.ProductName) && string.IsNullOrWhiteSpace(x.InitiativeName)))
                .WithMessage("Either productName or initiativeName is required.");
            RuleFor(x => x)
                .Must(x => !(x.ProductName is not null && x.InitiativeName is not null))
                .WithMessage("Provide exactly one of productName or initiativeName.");
            RuleFor(x => x.ProductName)
                .Must(n => n is null || (n.Trim() == n && !n.Any(char.IsControl)))
                .WithMessage("Product name must not have leading/trailing whitespace or control characters.")
                .MaximumLength(200)
                .When(x => x.ProductName is not null);
            RuleFor(x => x.InitiativeName).NotEmpty().MaximumLength(200)
                .When(x => x.InitiativeName is not null);
        }
    }

    public sealed class Handler(IProductGraph graph) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            if (request.ProductName is not null)
            {
                if (!await graph.ExistsAsync(request.ProductName, cancellationToken))
                {
                    throw new NotFoundException(
                        $"Product '{request.ProductName}' is not registered. Register it with 'upsert-product-name' first.");
                }

                var items = new List<ProductLinkItem>();
                foreach (string initiative in await graph.GetInitiativeNamesAsync(request.ProductName, cancellationToken))
                {
                    items.Add(new ProductLinkItem(request.ProductName, initiative, null));
                }

                foreach (string repo in await graph.GetRepoNamesAsync(request.ProductName, cancellationToken))
                {
                    items.Add(new ProductLinkItem(request.ProductName, null, repo));
                }

                return new Response(items);
            }

            string initiativeName = request.InitiativeName!;
            IReadOnlyList<string> products = await graph.GetProductsForInitiativeAsync(
                initiativeName, cancellationToken);
            return new Response(
                [.. products.Select(p => new ProductLinkItem(p, initiativeName, null))]);
        }
    }
}
