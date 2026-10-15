using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>
/// Declares <c>(Product)-[:HAS_REPO]->(Repo)</c> (D8, Part F). Refuses an unregistered product (404)
/// and a repo already linked to a different product (409 naming it). Idempotent for the same pair.
/// Dry run validates and reports without writing.
/// </summary>
public static class LinkProductRepo
{
    public sealed record Request(
        string ProductName,
        string Repo,
        string Reason,
        string? Source = null,
        bool DryRun = false) : IRequest<Response>;

    public sealed record Response(
        string ProductName,
        string Repo,
        bool Changed,
        string? ConflictProductName,
        bool DryRun);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.ProductName).SetValidator(ProductNameValidation.NameValidator);
            RuleFor(x => x.Repo).NotEmpty().MaximumLength(200)
                .Must(r => r == r.Trim()).WithMessage("Repo must not have leading or trailing whitespace.");
            RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000);
            RuleFor(x => x.Source).MaximumLength(100);
        }
    }

    public sealed class Handler(
        IProductGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Link product repo started");

            if (!await graph.ExistsAsync(request.ProductName, cancellationToken))
            {
                throw new NotFoundException(
                    $"Product '{request.ProductName}' is not registered. Register it with 'upsert-product-name' first.");
            }

            string? currentOwner = await graph.GetProductForRepoAsync(request.Repo, cancellationToken);
            string? conflict = currentOwner is not null && currentOwner != request.ProductName
                ? currentOwner
                : null;

            if (request.DryRun)
            {
                // A change happens only when the repo is free; an already-linked-to-this-product repo
                // is a no-op, and a repo owned by another product is a refusal, not a change.
                bool wouldChange = currentOwner is null;
                return new Response(request.ProductName, request.Repo, wouldChange, conflict, true);
            }

            if (conflict is not null)
            {
                throw new ConflictException(
                    $"Repo '{request.Repo}' is already linked to product '{conflict}'. Unlink it there or pick another product.");
            }

            RepoLinkResult result = await graph.LinkRepoAsync(
                request.ProductName, request.Repo, request.Reason, request.Source ?? "api", cancellationToken);

            // The pre-check above runs without the advisory lock, so a concurrent link to another
            // product can land between it and the locked write. That write reports the true owner; a
            // non-null owner here is a conflict, not a silent no-op.
            if (result.ExistingProductName is not null)
            {
                throw new ConflictException(
                    $"Repo '{request.Repo}' is already linked to product '{result.ExistingProductName}'. Unlink it there or pick another product.");
            }

            bool changed = result.Created;
            logger.LogInformation("Link product repo completed. Changed: {Changed}", changed);
            return new Response(request.ProductName, request.Repo, changed, null, false);
        }
    }
}
