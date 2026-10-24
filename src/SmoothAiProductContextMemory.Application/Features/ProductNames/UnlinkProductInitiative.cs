using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>
/// Removes exactly one <c>HAS_INITIATIVE</c> edge (D7). Refuses an unregistered product. Idempotent:
/// an absent pair is <c>changed: false</c>. Dry run removes nothing and reports whether an edge exists.
/// </summary>
public static class UnlinkProductInitiative
{
    public sealed record Request(
        string ProductName,
        string InitiativeName,
        bool DryRun = false) : IRequest<Response>;

    public sealed record Response(string ProductName, string InitiativeName, bool Changed, bool DryRun);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ProductName).SetValidator(ProductNameValidation.NameValidator);
            RuleFor(x => x.InitiativeName).NotEmpty().MaximumLength(200);
        }
    }

    public sealed class Handler(
        IProductGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Unlink product initiative started");

            if (!await graph.ExistsAsync(request.ProductName, cancellationToken))
            {
                throw new NotFoundException(
                    $"Product '{request.ProductName}' is not registered. Register it with 'upsert-product-name' first.");
            }

            bool linked = (await graph.GetInitiativeNamesAsync(request.ProductName, cancellationToken))
                .Contains(request.InitiativeName);

            if (request.DryRun)
            {
                return new Response(request.ProductName, request.InitiativeName, linked, true);
            }

            bool changed = linked && await graph.UnlinkInitiativeAsync(
                request.ProductName, request.InitiativeName, cancellationToken);

            logger.LogInformation("Unlink product initiative completed. Changed: {Changed}", changed);
            return new Response(request.ProductName, request.InitiativeName, changed, false);
        }
    }
}
