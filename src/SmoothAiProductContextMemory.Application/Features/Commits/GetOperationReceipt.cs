using FluentValidation;
using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Application.Features.Commits;

public static class GetOperationReceipt
{
    public sealed record Request(string OperationKey) : IRequest<OperationReceipt>;
    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(x => x.OperationKey).NotEmpty().MaximumLength(200);
    }

    public sealed class Handler(ICorpusCommitStore store) : IRequestHandler<Request, OperationReceipt>
    {
        public async ValueTask<OperationReceipt> Handle(Request request, CancellationToken cancellationToken) =>
            await store.FindAsync(request.OperationKey, cancellationToken)
                ?? throw new NotFoundException("Operation receipt was not found.");
    }
}
