using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Groups;

public static class AppendGroupDescription
{
    public sealed record Request(Guid GroupUuid, string Name, string Body) : IRequest<Response>;

    public sealed record Response(Guid GroupUuid, int Version, string Name);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.GroupUuid).NotEmpty();
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Body).NotEmpty();
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IDbErrorMapper errorMapper,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Append group description started");

            MemoryGroup group = await db.MemoryGroups
                .SingleOrDefaultAsync(g => g.Uuid == request.GroupUuid, cancellationToken)
                ?? throw new NotFoundException($"Group '{request.GroupUuid}' was not found.");

            int next = await db.GroupDescriptions
                .Where(d => d.GroupId == group.Id)
                .Select(d => d.Version)
                .DefaultIfEmpty()
                .MaxAsync(cancellationToken) + 1;

            var row = new GroupDescription
            {
                GroupId = group.Id,
                Version = next,
                Name = request.Name,
                Body = request.Body,
                CreatedOn = DateTimeOffset.UtcNow,
            };

            db.GroupDescriptions.Add(row);
            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            logger.LogInformation("Append group description completed. Version: {Version}", next);
            return new Response(group.Uuid, next, row.Name);
        }
    }
}
