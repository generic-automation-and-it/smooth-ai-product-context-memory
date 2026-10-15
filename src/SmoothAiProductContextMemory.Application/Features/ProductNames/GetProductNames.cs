using Mediator;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.ProductNames;

/// <summary>Lists every registered product with its initiative and repo counts (one hop each).</summary>
public static class GetProductNames
{
    public sealed record Request() : IRequest<Response>;

    public sealed record Response(IReadOnlyList<ProductRegistryRow> Items);

    public sealed class Handler(IProductGraph graph) : IRequestHandler<Request, Response>
    {
        public async ValueTask<Response> Handle(Request request, CancellationToken cancellationToken)
        {
            IReadOnlyList<ProductRegistryRow> items = await graph.ListAsync(cancellationToken);
            return new Response(items);
        }
    }
}
