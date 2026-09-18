using FluentValidation;
using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.RecallFeedback;

/// <summary>
/// NFR-03 question 2: how often does retrieval return nothing? Countable over a period, so a tuning
/// change can be shown to move it.
/// </summary>
public static class GetMissRate
{
    public sealed record Request(
        DateTimeOffset From,
        DateTimeOffset To) : IRequest<Response>;

    public sealed record Response(int Retrievals, int Misses, double MissRate);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From);
        }
    }

    public sealed class Handler(IRecallFeedbackQuery query) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            MissRateResult result =
                await query.MissRateAsync(new MissRateRequest(request.From, request.To), cancellationToken);

            return new Response(result.Retrievals, result.Misses, result.MissRate);
        }
    }
}
