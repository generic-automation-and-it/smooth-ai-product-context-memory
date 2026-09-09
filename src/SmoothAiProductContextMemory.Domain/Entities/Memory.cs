namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// The stable logical row for one atomic fact. The subject (<see cref="Description"/>) is stable by
/// definition and is what deduplication matches on, so it lives here; the claim lives on
/// <see cref="MemoryVersion"/>. Tags and facets are classification, not claims, and are therefore
/// unversioned — placing them on this row is what makes that true.
/// </summary>
public sealed class Memory
{
    public long Id { get; set; }

    /// <summary>Logical memory identity — stable across versions.</summary>
    public Guid Uuid { get; set; }

    /// <summary>Shared across clones in different groups; equals <see cref="Uuid"/> until a clone exists.</summary>
    public Guid LineageId { get; set; }

    public long GroupId { get; set; }

    public MemoryGroup? Group { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The subject — what this memory is about. Stable.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Slugified <see cref="Description"/>; unique per group as an exact-match backstop.</summary>
    public string SubjectSlug { get; set; } = string.Empty;

    /// <summary>Free text + AI keywords. Unversioned.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Controlled vocabulary. Unversioned; registry is advisory, not enforcing.</summary>
    public List<string> Facets { get; set; } = [];

    public ICollection<MemoryVersion> Versions { get; set; } = [];

    public ICollection<MemoryLink> LinksFrom { get; set; } = [];

    public ICollection<MemoryLink> LinksTo { get; set; } = [];
}
