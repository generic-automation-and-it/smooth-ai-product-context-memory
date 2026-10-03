using FluentValidation;
using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.Commits;

public static class GetCorpusState
{
    public sealed record Request : IRequest<CorpusState>;
    public sealed class Validator : AbstractValidator<Request> { }
    public sealed class Handler(ICorpusCommitStore store) : IRequestHandler<Request, CorpusState>
    {
        public async ValueTask<CorpusState> Handle(Request request, CancellationToken cancellationToken) =>
            await store.GetStateAsync(cancellationToken);
    }
}
