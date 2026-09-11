using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Labels;

public static class GetLabels
{
    public sealed record Request : IRequest<Response>;

    public sealed record LabelRow(string Name, string Status, long Uses);

    public sealed record Response(IReadOnlyList<LabelRow> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
        }
    }

    public sealed class Handler(IApplicationDbContext db, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Get labels started");

            List<Label> labels = await db.Labels.AsNoTracking().OrderBy(l => l.Name).ToListAsync(cancellationToken);
            Dictionary<string, long> usage = await db.QueryLabelUsage()
                .ToDictionaryAsync(r => r.Name, r => r.Uses, cancellationToken);

            List<LabelRow> items =
            [
                .. labels.Select(l => new LabelRow(l.Name, l.Status, usage.GetValueOrDefault(l.Name)))
            ];

            logger.LogInformation("Get labels completed. Count: {Count}", items.Count);
            return new Response(items);
        }
    }
}
