using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;

namespace SmoothAiProductContextMemory.Application.Common.Persistence;

/// <summary>
/// Resolves a page of memories' ticket binding from the two places the store keeps it.
///
/// <para>
/// **Owned** — the group's membership array (<c>memory_group.tickets</c>, GIN-indexed, unnested for an
/// exact identity match). This is the ownership authority: LADR-08 mirrors it into Ticket vertices
/// transactionally rather than replacing it.
/// </para>
///
/// <para>
/// **Referenced** — <c>(Memory)-[:ABOUT]->(Ticket)</c> edges, which exist only in the graph
/// (LADR-09): a memory may point at a ticket another group owns, and that binding is invisible to the
/// relational array. Reading only the array would show a card binding the memory does not have.
/// </para>
///
/// <para>
/// Both reads are bounded by the page (at most one query each, anchored on the selection) and neither
/// writes. The array is unnested in SQL rather than materialised as documents, so a row carries ticket
/// identity only.
/// </para>
/// </summary>
public static class TicketIdentityLookup
{
    /// <summary>Returns the items with <see cref="CheapMemory.Tickets"/> filled in.</summary>
    public static async Task<IReadOnlyList<CheapMemory>> EnrichAsync(
        IApplicationDbContext db,
        IMemoryGraph graph,
        IReadOnlyList<CheapMemory> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<TicketRef>> owned =
            await OwnedByGroupAsync(db, items.Select(i => i.GroupUuid).Distinct().ToArray(), cancellationToken);
        IReadOnlyList<MemoryTicketReference> references =
            await graph.FindAboutAsync([.. items.Select(i => i.Uuid).Distinct()], cancellationToken);

        var referencedByUuid = new Dictionary<Guid, List<TicketRef>>();
        foreach (MemoryTicketReference reference in references)
        {
            if (!referencedByUuid.TryGetValue(reference.MemoryUuid, out List<TicketRef>? refs))
            {
                refs = [];
                referencedByUuid[reference.MemoryUuid] = refs;
            }

            refs.Add(new TicketRef(reference.Provider, reference.Key, TicketMatchValue.Reference));
        }

        return
        [
            .. items.Select(item =>
            {
                List<TicketRef> tickets = [];
                if (owned.TryGetValue(item.GroupUuid, out IReadOnlyList<TicketRef>? ownedTickets))
                {
                    tickets.AddRange(ownedTickets);
                }

                if (referencedByUuid.TryGetValue(item.Uuid, out List<TicketRef>? refs))
                {
                    // An owned ticket stays owned: the group membership is the ownership authority, so a
                    // duplicate ABOUT edge on a ticket the group already owns is one binding, not two.
                    tickets.AddRange(refs.Where(reference => !tickets.Any(owned =>
                        owned.Match == TicketMatchValue.Owner
                        && owned.Provider == reference.Provider
                        && owned.Key == reference.Key)));
                }

                return item with { Tickets = tickets };
            })
        ];
    }

    /// <summary>
    /// The group's stored membership, unnested to identity by the provider. A malformed element (a
    /// shape without provider or key) is filtered out in SQL rather than failing the page: the array is
    /// append-only history and one bad element must not cost the practitioner every row.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TicketRef>>> OwnedByGroupAsync(
        IApplicationDbContext db,
        Guid[] groupUuids,
        CancellationToken cancellationToken)
    {
        var owned = new Dictionary<Guid, List<TicketRef>>();
        await foreach (OwnedTicketIdentity row in db
            .QueryOwnedTicketIdentities(groupUuids)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (!owned.TryGetValue(row.GroupUuid, out List<TicketRef>? refs))
            {
                refs = [];
                owned[row.GroupUuid] = refs;
            }

            TicketRef ticket = new(row.Provider, row.Key, TicketMatchValue.Owner);
            if (!refs.Contains(ticket))
            {
                refs.Add(ticket);
            }
        }

        return owned.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<TicketRef>)entry.Value);
    }
}
