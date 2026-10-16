namespace SmoothAiProductContextMemory.Application.Common.Persistence;

/// <summary>
/// One owned ticket identity, unnested from a group's membership. A projection, not an entity: it
/// exists so a read can name a memory's binding without materialising the group's stored ticket
/// documents.
/// </summary>
public sealed class OwnedTicketIdentity
{
    public Guid GroupUuid { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;
}
