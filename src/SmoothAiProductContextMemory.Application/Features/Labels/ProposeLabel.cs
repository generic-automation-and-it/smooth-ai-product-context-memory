using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Labels;

public static class ProposeLabel
{
    public sealed record Request(string Name) : IRequest<Response>;

    public sealed record Response(string Name, string Status);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        }
    }

    public sealed class Handler(
        IApplicationDbContext db,
        IDbErrorMapper errorMapper,
        ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Propose label started");

            Label? existing = await db.Labels.SingleOrDefaultAsync(l => l.Name == request.Name, cancellationToken);
            if (existing is not null)
            {
                logger.LogInformation("Propose label completed");
                return new Response(existing.Name, existing.Status);
            }

            var label = new Label
            {
                Name = request.Name,
                Status = Label.LabelStatus.Draft,
            };
            db.Labels.Add(label);
            await errorMapper.SaveOrMapAsync(() => db.SaveChangesAsync(cancellationToken));

            logger.LogInformation("Propose label completed");
            return new Response(label.Name, label.Status);
        }
    }
}
