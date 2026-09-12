using Mediator;
using Microsoft.AspNetCore.Mvc;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Application.Features.Initiatives;
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

        // scope is the caller declaring which dimension it is reading as. Without it, the scope rule
        // hides the same dimensions it hides from an open query — the proxy is the boundary, not a
        // way around it.
        group.MapGet("/memories/{uuid:guid}/versions/{version:int}/blob", async (
            Guid uuid,
            int version,
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] string? scope = null) =>
        {
            GetMemoryBlob.Response blob = await mediator.Send(new GetMemoryBlob.Request(uuid, version, scope), ct);
            return Results.File(blob.Content, blob.ContentType);
        });

        group.MapPost("/groups/resolve", (ResolveGroup.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));

        group.MapPatch("/groups/{uuid:guid}", (
            Guid uuid,
            UpdateGroup.Request body,
            IMediator mediator,
            CancellationToken ct) =>
            mediator.Send(body with { GroupUuid = uuid }, ct));

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

        group.MapGet("/initiatives", (
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] string? status = null) =>
            mediator.Send(new GetInitiatives.Request(status), ct));

        group.MapPost("/initiatives", (UpsertInitiative.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct));
    }
}
