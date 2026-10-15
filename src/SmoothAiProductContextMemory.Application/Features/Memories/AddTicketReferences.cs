using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

/// <summary>
/// Adds <c>ABOUT</c> references from already-existing memories to tickets they do not own — the
/// standalone surface for re-pointing existing memories (the retag worktask). Idempotent; a repeated
/// <c>(memory, ticket)</c> is counted as already present, not created.
/// </summary>
public static class AddTicketReferences
{
    public sealed record Request(
        IReadOnlyList<Guid> Uuids,
        IReadOnlyList<TicketReferenceInput> Tickets,
        string Reason) : IRequest<Response>;

    public sealed record Response(int Created, int AlreadyPresent);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Uuids).NotNull().NotEmpty();
            RuleFor(x => x.Uuids).Must(u => u.Count <= 200)
                .WithMessage("At most 200 memories may be referenced per request.");
            RuleFor(x => x.Tickets).NotNull().NotEmpty();
            RuleFor(x => x.Tickets).Must(t => t.Count <= 20)
                .WithMessage("At most 20 tickets may be referenced per request.");
            RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000);
            RuleForEach(x => x.Tickets).ChildRules(ticket =>
            {
                ticket.RuleFor(t => t.Provider).NotEmpty().MaximumLength(512);
                ticket.RuleFor(t => t.Key).NotEmpty().MaximumLength(512);
                ticket.RuleFor(t => t.Provider)
                    .Must(p => p is null || !p.Contains('\0'))
                    .WithMessage("Provider must not contain a null character.");
                ticket.RuleFor(t => t.Key)
                    .Must(k => k is null || !k.Contains('\0'))
                    .WithMessage("Key must not contain a null character.");
            });
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IMemoryGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Add ticket references started. Memories: {Count}", request.Uuids.Count);

            Memory[] memories = await db.Memories
                .Where(m => request.Uuids.Contains(m.Uuid))
                .ToArrayAsync(cancellationToken);
            if (memories.Length != request.Uuids.Distinct().Count())
            {
                Guid missing = request.Uuids.First(u => memories.All(m => m.Uuid != u));
                throw new NotFoundException($"Memory '{missing}' was not found.");
            }

            int created = 0;
            int alreadyPresent = 0;
            foreach (Memory memory in memories)
            {
                foreach (TicketReferenceInput ticket in request.Tickets)
                {
                    MemoryGroup? owner = await TicketLookup.FindGroupByTicketAsync(
                        db, ticket.Provider, ticket.Key, cancellationToken);
                    if (owner is null)
                    {
                        throw new NotFoundException($"Ticket '{ticket.Provider}:{ticket.Key}' was not found.");
                    }

                    if (owner.Id == memory.GroupId)
                    {
                        throw new ConflictException(
                            $"Ticket '{ticket.Provider}:{ticket.Key}' is owned by the memory's group; referencing an owned ticket is not required.");
                    }

                    bool edgeCreated = await graph.CreateAboutAsync(
                        memory.Uuid, ticket.Provider, ticket.Key, request.Reason, cancellationToken);
                    if (edgeCreated)
                    {
                        created++;
                    }
                    else
                    {
                        alreadyPresent++;
                    }
                }
            }

            logger.LogInformation(
                "Add ticket references completed. Created: {Created} AlreadyPresent: {AlreadyPresent}",
                created,
                alreadyPresent);
            return new Response(created, alreadyPresent);
        }
    }
}
