namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// A directed edge between two logical memories. Kept as a table because reverse traversal — what
/// points at this memory — is an index seek on a table but a containment scan as JSONB. Links hold
/// between two subjects and survive either side changing its claim.
/// </summary>
public sealed class MemoryLink
{
    public long SourceMemoryId { get; set; }

    public Memory? SourceMemory { get; set; }

    public long TargetMemoryId { get; set; }

    public Memory? TargetMemory { get; set; }

    public string Relation { get; set; } = string.Empty;

    /// <summary>Mandatory — why this link exists.</summary>
    public string Reason { get; set; } = string.Empty;

    public static class RelationValue
    {
        public const string DependsOn = "depends_on";
        public const string RelatesTo = "relates_to";
        public const string Contradicts = "contradicts";
        public const string Supersedes = "supersedes";
        public const string Implements = "implements";
    }
}
