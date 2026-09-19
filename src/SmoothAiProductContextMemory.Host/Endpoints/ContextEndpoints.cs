using Mediator;
using Microsoft.AspNetCore.Mvc;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Application.Features.Initiatives;
using SmoothAiProductContextMemory.Application.Features.Labels;
using SmoothAiProductContextMemory.Application.Features.Links;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Application.Features.Preflight;
using SmoothAiProductContextMemory.Application.Features.RecallFeedback;
using SmoothAiProductContextMemory.Application.Features.Tickets;
using SmoothAiProductContextMemory.Host.Configuration;

namespace SmoothAiProductContextMemory.Host.Endpoints;

internal static class ContextEndpoints
{
    public static void Map(WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/context");

        group.MapPost("/preflight", (Preflight.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapPost("/memories", (
            [FromBody] SetMemories.Request body,
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] bool dryRun = false) =>
            mediator.Send(body with { DryRun = dryRun || body.DryRun }, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapPost("/query", (QueryMemories.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Read);

        group.MapGet("/memories/{uuid:guid}/versions", (
            Guid uuid,
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] string? scope = null) =>
            mediator.Send(new GetMemoryVersions.Request(uuid, scope), ct))
            .RequireCapability(ApiCapability.Read);

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
        }).RequireCapability(ApiCapability.Read);

        group.MapPost("/groups/resolve", (ResolveGroup.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapPatch("/groups/{uuid:guid}", (
            Guid uuid,
            UpdateGroup.Request body,
            IMediator mediator,
            CancellationToken ct) =>
            mediator.Send(body with { GroupUuid = uuid }, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapPost("/groups/{uuid:guid}/descriptions", (
            Guid uuid,
            AppendGroupDescription.Request body,
            IMediator mediator,
            CancellationToken ct) =>
            mediator.Send(body with { GroupUuid = uuid }, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapPost("/links", (CreateLink.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Write);

        // maxDepth is on the body, not defaulted server-side: a bound the caller never stated is a
        // bound the caller never considered, and nothing in the graph store stops an unbounded walk.
        group.MapPost("/paths", (FindPaths.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Read);

        group.MapPut("/tickets/parent", (SetTicketParent.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapPost("/tickets/paths", (FindTicketPaths.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Read);

        group.MapGet("/labels", (IMediator mediator, CancellationToken ct) =>
            mediator.Send(new GetLabels.Request(), ct))
            .RequireCapability(ApiCapability.Read);

        group.MapPost("/labels", (ProposeLabel.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Write);

        group.MapGet("/initiatives", (
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] string? status = null) =>
            mediator.Send(new GetInitiatives.Request(status), ct))
            .RequireCapability(ApiCapability.Read);

        group.MapPost("/initiatives", (UpsertInitiative.Request body, IMediator mediator, CancellationToken ct) =>
            mediator.Send(body, ct))
            .RequireCapability(ApiCapability.Write);

        // Practitioner tuning surface (HLD-004 NFR-03). Consumed occasionally by a human; not the
        // retrieval path, and never pulls raw feedback into retrieval. identity/count/time only.
        group.MapGet("/recall-feedback/never-recalled", (
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] DateTimeOffset asOf,
            [FromQuery] int? limit = null) =>
            mediator.Send(new GetNeverRecalledMemories.Request(asOf, limit ?? 500), ct))
            .RequireCapability(ApiCapability.Read);

        group.MapGet("/recall-feedback/miss-rate", (
            IMediator mediator,
            CancellationToken ct,
            [FromQuery] DateTimeOffset from,
            [FromQuery] DateTimeOffset to) =>
            mediator.Send(new GetMissRate.Request(from, to), ct))
            .RequireCapability(ApiCapability.Read);

        // Resettable baseline (LADR-04): feedback is disposable, so this is legitimate, not destructive.
        group.MapPost("/recall-feedback/reset", (IMediator mediator, CancellationToken ct) =>
            mediator.Send(new ResetRecallFeedback.Request(), ct))
            .RequireCapability(ApiCapability.Write);
    }
}
