using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Links;

public static class CreateLink
{
    public sealed record Request(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason) : IRequest<Response>;

    public sealed record Response(Guid SourceUuid, Guid TargetUuid, string Relation);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.SourceUuid).NotEmpty();
            RuleFor(x => x.TargetUuid).NotEmpty();
            RuleFor(x => x.Relation).NotEmpty().MaximumLength(32);
            RuleFor(x => x.Reason).NotEmpty();
            RuleFor(x => x)
                .Must(x => x.SourceUuid != x.TargetUuid)
                .WithMessage("A link cannot target itself.");
        }
    }

    public sealed class Handler(IApplicationDbContext db, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Create link started");

            Memory source = await db.Memories.SingleOrDefaultAsync(m => m.Uuid == request.SourceUuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{request.SourceUuid}' was not found.");
            Memory target = await db.Memories.SingleOrDefaultAsync(m => m.Uuid == request.TargetUuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{request.TargetUuid}' was not found.");

            bool exists = await db.MemoryLinks.AnyAsync(
                l => l.SourceMemoryId == source.Id
                    && l.TargetMemoryId == target.Id
                    && l.Relation == request.Relation,
                cancellationToken);
            if (exists)
            {
                throw new ConflictException("Link already exists.");
            }

            db.MemoryLinks.Add(new MemoryLink
            {
                SourceMemoryId = source.Id,
                TargetMemoryId = target.Id,
                Relation = request.Relation,
                Reason = request.Reason,
            });

            await DbExceptionMapping.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            logger.LogInformation("Create link completed");
            return new Response(source.Uuid, target.Uuid, request.Relation);
        }
    }
}
