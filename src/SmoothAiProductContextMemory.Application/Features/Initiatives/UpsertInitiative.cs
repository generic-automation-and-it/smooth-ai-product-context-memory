using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Initiatives;

/// <summary>
/// Creates an initiative, or updates the supplied fields of an existing one. Keyed by name.
/// </summary>
/// <remarks>
/// Idempotent by name rather than create-only: the name is the wire identity (the entity has no
/// uuid), so a caller that cannot tell whether an initiative already exists must still be able to
/// ensure it does. Archiving is the same call with <c>status</c>. Null leaves a field unchanged.
/// </remarks>
public static class UpsertInitiative
{
    public sealed record Request(string Name, string? Description = null, string? Status = null) : IRequest<Response>;

    public sealed record Response(string Name, string Description, string Status, bool Created);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Description).MaximumLength(1000);
            RuleFor(x => x.Status)
                .Must(s => s is null
                    || s == Initiative.InitiativeStatus.Active
                    || s == Initiative.InitiativeStatus.Archived)
                .WithMessage("Status must be active or archived.");
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IDbErrorMapper errorMapper,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Upsert initiative started");

            Initiative? existing = await db.Initiatives
                .SingleOrDefaultAsync(i => i.Name == request.Name, cancellationToken);

            bool created = existing is null;
            Initiative initiative = existing ?? new Initiative { Name = request.Name };

            if (request.Description is not null)
            {
                initiative.Description = request.Description;
            }

            if (request.Status is not null)
            {
                initiative.Status = request.Status;
            }

            if (created)
            {
                db.Initiatives.Add(initiative);
            }

            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            logger.LogInformation("Upsert initiative completed. Created: {Created}", created);
            return new Response(initiative.Name, initiative.Description, initiative.Status, created);
        }
    }
}
