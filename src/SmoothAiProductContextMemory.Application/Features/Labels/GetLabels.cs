using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Features.Labels;

/// <summary>
/// The facet vocabulary as it actually is: the derived <c>label_usage</c> view unioned with the
/// advisory registry.
/// </summary>
/// <remarks>
/// Usage is derived from the facets in use, and the registry is deliberately non-enforcing, so the
/// two sets do not coincide: a facet can be in heavy use and never registered, and a registered
/// label can have no uses. Starting from the registry alone would hide exactly the vocabulary drift
/// this endpoint exists to reveal. <see cref="LabelRow.Status"/> is null for a facet in use that has
/// no registry row.
/// </remarks>
public static class GetLabels
{
    public sealed record Request : IRequest<Response>;

    public sealed record LabelRow(string Name, string? Status, long Uses);

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

            List<LabelUsageRow> usage = await db.QueryLabelUsage().ToListAsync(cancellationToken);
            List<Label> registered = await db.Labels.AsNoTracking().ToListAsync(cancellationToken);

            Dictionary<string, long> uses = usage
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Uses), StringComparer.Ordinal);
            Dictionary<string, string> statuses = registered
                .GroupBy(l => l.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);

            List<LabelRow> items =
            [
                .. uses.Keys
                    .Union(statuses.Keys, StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Select(name => new LabelRow(
                        name,
                        statuses.GetValueOrDefault(name),
                        uses.GetValueOrDefault(name)))
            ];

            logger.LogInformation("Get labels completed. Count: {Count}", items.Count);
            return new Response(items);
        }
    }
}
