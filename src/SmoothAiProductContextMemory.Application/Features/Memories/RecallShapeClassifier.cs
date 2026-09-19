using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Application.Features.Memories;

/// <summary>
/// Maps a retrieval request to a bounded retrieval-shape category (HLD-004 LADR-03). Classifies the
/// raw request, not the resolved criteria — the shape is the kind of call the caller made. Pure, so
/// it can only ever return one of the <see cref="RetrievalShape"/> constants, never the query text.
/// </summary>
public static class RecallShapeClassifier
{
    public static string Classify(QueryMemories.Request request)
    {
        bool hasTicket = !string.IsNullOrWhiteSpace(request.TicketProvider)
            && !string.IsNullOrWhiteSpace(request.TicketKey);
        bool hasFreeText = !string.IsNullOrWhiteSpace(request.Query);
        bool hasFacets = request.Facets is { Count: > 0 };

        bool hasOtherFilter = !string.IsNullOrWhiteSpace(request.Kind)
            || !string.IsNullOrWhiteSpace(request.Status)
            || !string.IsNullOrWhiteSpace(request.Repo)
            || !string.IsNullOrWhiteSpace(request.InitiativeName)
            || !string.IsNullOrWhiteSpace(request.ScopeDimension)
            || request.Tags is { Count: > 0 };

        // Scope predicates are the strongest in-context signal and win over free text: a ticket- or
        // group-scoped retrieval is primarily scoped.
        if (hasTicket)
        {
            return RetrievalShape.TicketScoped;
        }

        if (request.GroupUuid is not null)
        {
            return RetrievalShape.GroupScoped;
        }

        if (hasFreeText)
        {
            return RetrievalShape.FreeText;
        }

        if (hasFacets && !hasOtherFilter)
        {
            return RetrievalShape.FacetOnly;
        }

        return RetrievalShape.Unfiltered;
    }
}
