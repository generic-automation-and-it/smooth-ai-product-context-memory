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
        DateTimeOffset To,
        string? RecallPurpose = null) : IRequest<Response>;

    public sealed record Response(int Retrievals, int Misses, double MissRate,
        int CallerRequests = 0, int CallerMisses = 0, double CallerMissRate = 0);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From);
            RuleFor(x => x.RecallPurpose).Must(purpose => Abstractions.RecallPurpose.IsFilter(purpose!)).When(x => x.RecallPurpose is not null);
        }
    }

    public sealed class Handler(IRecallFeedbackQuery query) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            MissRateResult result =
                await query.MissRateAsync(new MissRateRequest(request.From, request.To, request.RecallPurpose), cancellationToken);

            return new Response(result.Retrievals, result.Misses, result.MissRate,
                result.CallerRequests, result.CallerMisses, result.CallerMissRate);
        }
    }
}
