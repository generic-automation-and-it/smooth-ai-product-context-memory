using FluentValidation;
using FluentValidation.Results;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Groups;

/// <summary>
/// Sets repository, initiative or scope on an existing group.
/// </summary>
/// <remarks>
/// Resolve-or-create only ever sets these on creation, so without this a group's repo or scope could
/// never be corrected — the group row itself is mutable state (only version history is append-only).
/// Null means "leave unchanged"; the group's tickets are not edited here because they accumulate
/// through resolve.
/// </remarks>
public static class UpdateGroup
{
    public sealed record Request(
        Guid GroupUuid,
        string? Repo,
        string? RepoUrl,
        string? InitiativeName,
        string? ScopeDimension,
        string? ScopeIdentifier) : IRequest<Response>;

    public sealed record Response(
        Guid Uuid,
        string ScopeDimension,
        string? ScopeIdentifier,
        string? Repo,
        string InitiativeName,
        IReadOnlyList<TicketInput> Tickets);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.GroupUuid).NotEmpty();
            RuleFor(x => x.ScopeDimension)
                .Must(d => d is null
                    || d == MemoryGroup.ScopeDimensionValue.Product
                    || d == MemoryGroup.ScopeDimensionValue.Customer
                    || d == MemoryGroup.ScopeDimensionValue.Program
                    || d == MemoryGroup.ScopeDimensionValue.Self)
                .WithMessage("ScopeDimension must be product, customer, program, or self.");
            RuleFor(x => x.Repo).MaximumLength(200);
            RuleFor(x => x.InitiativeName).MaximumLength(200);
            RuleFor(x => x.ScopeIdentifier).MaximumLength(200);
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IDbErrorMapper errorMapper,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Update group started");

            MemoryGroup group = await db.MemoryGroups
                .SingleOrDefaultAsync(g => g.Uuid == request.GroupUuid, cancellationToken)
                ?? throw new NotFoundException($"Group '{request.GroupUuid}' was not found.");

            if (request.Repo is not null)
            {
                group.Repo = request.Repo;
            }

            if (request.RepoUrl is not null)
            {
                group.RepoUrl = request.RepoUrl;
            }

            if (request.ScopeDimension is not null)
            {
                group.ScopeDimension = request.ScopeDimension;
            }

            if (request.ScopeIdentifier is not null)
            {
                group.ScopeIdentifier = request.ScopeIdentifier;
            }

            // The identifier requirement depends on stored state, so it cannot live in the validator:
            // a request that only changes the dimension has to be judged against the existing one.
            if (group.ScopeDimension is MemoryGroup.ScopeDimensionValue.Customer
                    or MemoryGroup.ScopeDimensionValue.Program
                && string.IsNullOrWhiteSpace(group.ScopeIdentifier))
            {
                throw new ValidationException(
                [
                    new ValidationFailure(
                        nameof(Request.ScopeIdentifier),
                        $"ScopeIdentifier is required for '{group.ScopeDimension}' scope.")
                ]);
            }

            Initiative initiative;
            if (request.InitiativeName is not null)
            {
                initiative = await db.Initiatives
                    .SingleOrDefaultAsync(i => i.Name == request.InitiativeName, cancellationToken)
                    ?? throw new NotFoundException($"Initiative '{request.InitiativeName}' was not found.");
                group.InitiativeId = initiative.Id;
            }
            else
            {
                initiative = await db.Initiatives
                    .SingleOrDefaultAsync(i => i.Id == group.InitiativeId, cancellationToken)
                    ?? throw new NotFoundException($"Initiative '{group.InitiativeId}' was not found.");
            }

            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            logger.LogInformation("Update group completed");
            return new Response(
                group.Uuid,
                group.ScopeDimension,
                group.ScopeIdentifier,
                group.Repo,
                initiative.Name,
                [.. group.Tickets.Select(t => new TicketInput(t.Provider, t.Key, t.Url))]);
        }
    }
}
