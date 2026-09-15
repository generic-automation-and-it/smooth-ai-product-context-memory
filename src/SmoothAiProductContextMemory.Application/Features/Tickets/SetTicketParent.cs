using System.Text.Json.Serialization;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.Tickets;

public static class SetTicketParent
{
    public sealed record Request(
        TicketIdentity Child,
        [property: JsonRequired] TicketIdentity? Parent,
        TicketIdentity? ExpectedParent,
        string Reason,
        string Source,
        DateTimeOffset? ObservedAt = null) : IRequest<Response>;

    public sealed record Response(bool Changed);

    internal sealed class IdentityValidator : AbstractValidator<TicketIdentity>
    {
        public IdentityValidator()
        {
            RuleFor(x => x.Provider).NotEmpty().MaximumLength(512);
            RuleFor(x => x.Key).NotEmpty().MaximumLength(512);
            RuleFor(x => x.Provider).Must(value => value is null || !value.Contains('\0'))
                .WithMessage("Provider must not contain a null character.");
            RuleFor(x => x.Key).Must(value => value is null || !value.Contains('\0'))
                .WithMessage("Key must not contain a null character.");
        }
    }

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(x => x.Child).NotNull().SetValidator(new IdentityValidator());
            RuleFor(x => x.Parent).SetValidator(new IdentityValidator()!);
            RuleFor(x => x.ExpectedParent).SetValidator(new IdentityValidator()!);
            RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000);
            RuleFor(x => x.Source).NotEmpty().MaximumLength(4000);
            RuleFor(x => x.Reason).Must(value => value is null || !value.Contains('\0'))
                .WithMessage("Reason must not contain a null character.");
            RuleFor(x => x.Source).Must(value => value is null || !value.Contains('\0'))
                .WithMessage("Source must not contain a null character.");
            RuleFor(x => x.Parent)
                .Must((request, parent) => parent is null || parent != request.Child)
                .WithMessage("A ticket cannot be its own parent.");
        }
    }

    public sealed class Handler(ITicketGraph graph, ILogger<Handler> logger) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Set ticket parent started");
            bool changed = await graph.ChangeParentAsync(
                new TicketParentChange(request.Child, request.Parent, request.ExpectedParent,
                    request.Reason, request.Source, request.ObservedAt), cancellationToken);
            logger.LogInformation("Set ticket parent completed. Changed: {Changed}", changed);
            return new Response(changed);
        }
    }
}
