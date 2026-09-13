using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
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

    public sealed class Handler(
        IApplicationDbContext db,
        IMemoryGraph graph,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Create link started");

            Memory source = await db.Memories.SingleOrDefaultAsync(m => m.Uuid == request.SourceUuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{request.SourceUuid}' was not found.");
            Memory target = await db.Memories.SingleOrDefaultAsync(m => m.Uuid == request.TargetUuid, cancellationToken)
                ?? throw new NotFoundException($"Memory '{request.TargetUuid}' was not found.");

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (await graph.ExistsAsync(source.Uuid, target.Uuid, request.Relation, cancellationToken)
                    || !await graph.CreateAsync(source.Uuid, target.Uuid, request.Relation, request.Reason, cancellationToken))
                {
                    throw new ConflictException("Link already exists.");
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }

            logger.LogInformation("Create link completed");
            return new Response(source.Uuid, target.Uuid, request.Relation);
        }
    }
}
