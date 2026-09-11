using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Initiatives;

/// <summary>
/// The initiative registry. Groups reference an initiative by name, so a caller resolving a group
/// needs a way to discover which names exist.
/// </summary>
public static class GetInitiatives
{
    public sealed record Request(string? Status = null) : IRequest<Response>;

    public sealed record InitiativeRow(string Name, string Description, string Status);

    public sealed record Response(IReadOnlyList<InitiativeRow> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Status)
                .Must(s => s is null
                    || s == Initiative.InitiativeStatus.Active
                    || s == Initiative.InitiativeStatus.Archived)
                .WithMessage("Status must be active or archived.");
        }
    }

    public sealed class Handler(IApplicationDbContext db, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Get initiatives started");

            IQueryable<Initiative> query = db.Initiatives.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                query = query.Where(i => i.Status == request.Status);
            }

            List<InitiativeRow> items = await query
                .OrderBy(i => i.Name)
                .Select(i => new InitiativeRow(i.Name, i.Description, i.Status))
                .ToListAsync(cancellationToken);

            logger.LogInformation("Get initiatives completed. Count: {Count}", items.Count);
            return new Response(items);
        }
    }
}
