namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// The umbrella over a set of memories. Keyed by its own <see cref="Uuid"/> (not by its tickets,
/// which accumulate over time). Holds many tickets in JSONB, at most one nullable repository, and
/// the scope. Children inherit repository, scope and initiative from this row.
/// </summary>
public sealed class MemoryGroup
{
    public long Id { get; set; }

    public Guid Uuid { get; set; }

    public string ScopeDimension { get; set; } = ScopeDimensionValue.Product;

    public string? ScopeIdentifier { get; set; }

    public long InitiativeId { get; set; }

    public Initiative? Initiative { get; set; }

    public string? Repo { get; set; }

    public string? RepoUrl { get; set; }

    /// <summary>JSONB array of ticket documents, each carrying its own <c>v</c> shape marker.</summary>
    public List<TicketDocument> Tickets { get; set; } = [];

    public DateTimeOffset CreatedOn { get; set; }

    public ICollection<GroupDescription> Descriptions { get; set; } = [];

    public ICollection<Memory> Memories { get; set; } = [];

    public static class ScopeDimensionValue
    {
        public const string Product = "product";
        public const string Customer = "customer";
        public const string Program = "program";
        public const string Self = "self";
    }
}
