using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.RecallFeedback;

/// <summary>
/// Resets the feedback baseline (LADR-04). Feedback is disposable, so resetting is legitimate, not
/// destructive — a tuning experiment must be able to start from a clean baseline.
/// </summary>
public static class ResetRecallFeedback
{
    public sealed record Request : IRequest<Response>;

    public sealed record Response(int RecordsDeleted);

    public sealed class Handler(IRecallFeedbackQuery query) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            int deleted = await query.ResetAsync(cancellationToken);
            return new Response(deleted);
        }
    }
}
