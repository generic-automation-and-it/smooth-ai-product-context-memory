using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Groups;

public static class ResolveGroup
{
    public sealed record Request(
        IReadOnlyList<TicketInput>? Tickets,
        string? Repo,
        string? RepoUrl,
        string? InitiativeName,
        string? ScopeDimension,
        string? ScopeIdentifier,
        string? Name,
        string? Body,
        string? OperationKey = null,
        Guid? ExpectedCorpusEpoch = null,
        long? ExpectedCorpusRevision = null) : IRequest<Response>;

    public sealed record Response(
        Guid Uuid,
        bool Created,
        string ScopeDimension,
        string? ScopeIdentifier,
        string? Repo,
        string InitiativeName,
        IReadOnlyList<TicketInput> Tickets);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.OperationKey).NotEmpty().MaximumLength(200).When(x => x.OperationKey is not null);
            RuleFor(x => x.ExpectedCorpusEpoch).NotNull().NotEqual(Guid.Empty).When(x => x.OperationKey is not null);
            RuleFor(x => x.ExpectedCorpusRevision).GreaterThanOrEqualTo(0).When(x => x.ExpectedCorpusRevision is not null);
            RuleForEach(x => x.Tickets).SetValidator(new TicketInputValidator());

            RuleFor(x => x.ScopeDimension)
                .Must(d => d is null
                    || d == MemoryGroup.ScopeDimensionValue.Product
                    || d == MemoryGroup.ScopeDimensionValue.Customer
                    || d == MemoryGroup.ScopeDimensionValue.Program
                    || d == MemoryGroup.ScopeDimensionValue.Self)
                .WithMessage("ScopeDimension must be product, customer, program, or self.");

            RuleFor(x => x.ScopeIdentifier)
                .NotEmpty()
                .When(x => x.ScopeDimension is MemoryGroup.ScopeDimensionValue.Customer
                    or MemoryGroup.ScopeDimensionValue.Program);
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IDbErrorMapper errorMapper,
        ITicketGraph ticketGraph,
        ILogger<Handler> logger,
        ICorpusCommitStore commitStore) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Group resolve started");

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            CorpusState state = await commitStore.LockAsync(cancellationToken);
            CorpusCommit.CheckEpoch(state, request.ExpectedCorpusEpoch);
            string hash = CorpusCommit.Hash(request with { OperationKey = null });
            if (request.OperationKey is { } operationKey
                && await commitStore.FindAsync(operationKey, cancellationToken) is { } prior)
            {
                return CorpusCommit.Replay<Response>(prior, hash, "group_resolve");
            }

            CorpusCommit.CheckRevision(state, request.ExpectedCorpusRevision);
            db.DiscardTrackedState();
            await ticketGraph.LockAsync(cancellationToken);

            MemoryGroup? existing = null;
            if (request.Tickets is { Count: > 0 })
            {
                foreach (TicketInput ticket in request.Tickets)
                {
                    MemoryGroup? owner = await TicketLookup.FindGroupByTicketAsync(
                        db, ticket.Provider, ticket.Key, cancellationToken);
                    if (owner is not null)
                    {
                        if (existing is not null && existing.Id != owner.Id)
                        {
                            throw new ConflictException("Supplied tickets do not resolve to a single group.");
                        }

                        existing = owner;
                    }
                }
            }

            if (existing is not null)
            {
                Initiative? existingInitiative = await db.Initiatives
                    .AsNoTracking()
                    .SingleOrDefaultAsync(i => i.Id == existing.InitiativeId, cancellationToken);
                Response response = ToResponse(existing, existingInitiative?.Name ?? "to-be-decided", created: false);
                await RecordAsync(request, hash, response, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Group resolve completed. Created: {Created}", false);
                return response;
            }

            string initiativeName = string.IsNullOrWhiteSpace(request.InitiativeName)
                ? "to-be-decided"
                : request.InitiativeName;

            Initiative initiative = await db.Initiatives
                .SingleOrDefaultAsync(i => i.Name == initiativeName, cancellationToken)
                ?? throw new NotFoundException($"Initiative '{initiativeName}' was not found.");

            List<TicketDocument> tickets = request.Tickets is { Count: > 0 }
                ? [.. request.Tickets.Select(t => TicketDocument.Create(t.Provider, t.Key, t.Url))]
                : [TicketDocument.Create("local", $"local:{Guid.NewGuid():N}", string.Empty)];

            var group = new MemoryGroup
            {
                Uuid = Guid.NewGuid(),
                ScopeDimension = request.ScopeDimension ?? MemoryGroup.ScopeDimensionValue.Product,
                ScopeIdentifier = request.ScopeIdentifier,
                InitiativeId = initiative.Id,
                Repo = request.Repo,
                RepoUrl = request.RepoUrl,
                Tickets = tickets,
                CreatedOn = DateTimeOffset.UtcNow,
            };

            db.MemoryGroups.Add(group);

            if (!string.IsNullOrWhiteSpace(request.Name) || !string.IsNullOrWhiteSpace(request.Body))
            {
                db.GroupDescriptions.Add(new GroupDescription
                {
                    Group = group,
                    Version = 1,
                    Name = request.Name ?? string.Empty,
                    Body = request.Body ?? string.Empty,
                    CreatedOn = DateTimeOffset.UtcNow,
                });
            }

            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));
            Response createdResponse = ToResponse(group, initiative.Name, created: true);
            await RecordAsync(request, hash, createdResponse, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Group resolve completed. Created: {Created}", true);
            return createdResponse;
        }

        private async Task RecordAsync(Request request, string hash, Response response, CancellationToken cancellationToken)
        {
            if (request.OperationKey is { } key)
            {
                await commitStore.RecordAsync(CorpusCommit.Receipt(key, hash, "group_resolve", response), cancellationToken);
            }
        }

        private static Response ToResponse(MemoryGroup group, string initiativeName, bool created) =>
            new(
                group.Uuid,
                created,
                group.ScopeDimension,
                group.ScopeIdentifier,
                group.Repo,
                initiativeName,
                [.. group.Tickets.Select(t => new TicketInput(t.Provider, t.Key, t.Url))]);
    }

}
