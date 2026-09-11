using Mediator;
using Microsoft.AspNetCore.Mvc;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Application.Features.Labels;
using SmoothAiProductContextMemory.Application.Features.Links;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Application.Features.Preflight;

namespace SmoothAiProductContextMemory.Host.Endpoints;

internal static class ContextEndpoints
{
    public static void Map(WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/context");

        group.MapPost("/preflight", (Preflight.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));

        group.MapPost("/memories", (
            [FromBody] SetMemories.Request body,
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] bool dryRun = false) =>
            mediator.Send(body with { DryRun = dryRun }, ct));

        group.MapPost("/query", (QueryMemories.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));

        group.MapGet("/memories/{uuid:guid}/versions", (Guid uuid, IMediator mediator, CancellationToken ct) =>
            mediator.Send(new GetMemoryVersions.Request(uuid), ct));

        group.MapGet("/memories/{uuid:guid}/versions/{version:int}/blob", async (
            Guid uuid,
            int version,
            IMediator mediator,
            CancellationToken ct) =>
        {
            GetMemoryBlob.Response blob = await mediator.Send(new GetMemoryBlob.Request(uuid, version), ct);
            return Results.File(blob.Content, blob.ContentType);
        });

        group.MapPost("/groups/resolve", (ResolveGroup.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));

        group.MapPost("/groups/{uuid:guid}/descriptions", (
            Guid uuid,
            AppendGroupDescription.Request body,
            IMediator mediator,
            CancellationToken ct) =>
            mediator.Send(body with { GroupUuid = uuid }, ct));

        group.MapPost("/links", (CreateLink.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));

        group.MapGet("/labels", (IMediator mediator, CancellationToken ct) =>
            mediator.Send(new GetLabels.Request(), ct));

        group.MapPost("/labels", (ProposeLabel.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));
    }
}
