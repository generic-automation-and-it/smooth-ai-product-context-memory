using FluentValidation;
using Mediator;

namespace SmoothAiProductContextMemory.Application.Common.Pipelines;

public sealed class ValidationBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        IValidator<TMessage>[] list = validators as IValidator<TMessage>[] ?? [.. validators];
        if (list.Length == 0)
        {
            return await next(message, cancellationToken);
        }

        var context = new ValidationContext<TMessage>(message);
        FluentValidation.Results.ValidationResult[] results = await Task.WhenAll(
            list.Select(v => v.ValidateAsync(context, cancellationToken)));

        FluentValidation.Results.ValidationFailure[] failures =
        [
            .. results.SelectMany(r => r.Errors).Where(e => e is not null)
        ];

        if (failures.Length > 0)
        {
            throw new ValidationException(failures);
        }

        return await next(message, cancellationToken);
    }
}
