using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>
/// Declares <c>(Product)-[:HAS_INITIATIVE]->(Initiative)</c>. Refuses an unregistered product (404
/// naming the register command) or an initiative that does not exist relationally (404). Idempotent:
/// an already-present pair is <c>changed: false</c>. Dry run validates and reports without writing.
/// </summary>
public static class LinkProductInitiative
{
    public sealed record Request(
        string ProductName,
        string InitiativeName,
        string Reason,
        string? Source = null,
        bool DryRun = false) : IRequest<Response>;

    public sealed record Response(string ProductName, string InitiativeName, bool Changed, bool DryRun);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ProductName).SetValidator(ProductNameValidation.NameValidator);
            RuleFor(x => x.InitiativeName).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000);
            RuleFor(x => x.Source).MaximumLength(100);
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IProductGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Link product initiative started");

            if (!await graph.ExistsAsync(request.ProductName, cancellationToken))
            {
                throw new NotFoundException(
                    $"Product '{request.ProductName}' is not registered. Register it with 'upsert-product-name' first.");
            }

            bool initiativeExists = await db.Initiatives
                .AnyAsync(i => i.Name == request.InitiativeName, cancellationToken);
            if (!initiativeExists)
            {
                throw new NotFoundException($"Initiative '{request.InitiativeName}' was not found.");
            }

            if (request.DryRun)
            {
                bool wouldLink = !(await graph.GetInitiativeNamesAsync(request.ProductName, cancellationToken))
                    .Contains(request.InitiativeName);
                return new Response(request.ProductName, request.InitiativeName, wouldLink, true);
            }

            bool changed = await graph.LinkInitiativeAsync(
                request.ProductName,
                request.InitiativeName,
                request.Reason,
                request.Source ?? "api",
                cancellationToken);

            logger.LogInformation("Link product initiative completed. Changed: {Changed}", changed);
            return new Response(request.ProductName, request.InitiativeName, changed, false);
        }
    }
}
