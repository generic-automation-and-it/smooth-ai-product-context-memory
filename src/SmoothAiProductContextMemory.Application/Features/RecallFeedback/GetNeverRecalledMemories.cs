using FluentValidation;
using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.RecallFeedback;

/// <summary>
/// NFR-03 question 1: which memories have never been recalled? Distinguishes "never recalled" from
/// "recently captured and not yet retrieved" so a new memory does not pollute the signal.
/// </summary>
public static class GetNeverRecalledMemories
{
    public sealed record Request(
        DateTimeOffset AsOf,
        int? Limit = 500) : IRequest<Response>;

    public sealed record Response(IReadOnlyList<NeverRecalledRow> Items);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Limit).InclusiveBetween(1, 5000).When(x => x.Limit is not null);
        }
    }

    public sealed class Handler(IRecallFeedbackQuery query) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            IReadOnlyList<NeverRecalledRow> items =
                await query.NeverRecalledAsync(
                    new NeverRecalledRequest(request.AsOf, request.Limit), cancellationToken);

            return new Response(items);
        }
    }
}
