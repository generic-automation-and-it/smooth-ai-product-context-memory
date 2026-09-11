namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// One version of a memory's claim. Append-only — a changed claim on an unchanged subject is a new
/// row with a higher <see cref="Version"/>, never an update. Carries both time axes:
/// <see cref="ValidFrom"/>/<see cref="ValidUntil"/> (business time) and <see cref="CreatedOn"/>
/// (system time).
/// </summary>
public sealed class MemoryVersion
{
    public long Id { get; set; }

    public long MemoryId { get; set; }

    public Memory? Memory { get; set; }

    public int Version { get; set; }

    /// <summary>Exactly one current version per memory; enforced by a partial unique index.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>The claim — the fact asserted. Volatile.</summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>AI-generated TL;DR of the blob content. Distinct from <see cref="Statement"/>.</summary>
    public string ContentSummary { get; set; } = string.Empty;

    /// <summary>Content address from blob storage; see ADR-0001.</summary>
    public string? BlobAddress { get; set; }

    /// <summary>Open vocabulary — not an enum.</summary>
    public string Kind { get; set; } = string.Empty;

    public short Confidence { get; set; }

    public string Status { get; set; } = MemoryVersionStatus.Proposed;

    /// <summary>JSONB array of source documents, each carrying its own <c>v</c> shape marker.</summary>
    public List<SourceDocument> Sources { get; set; } = [];

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }

    public DateTimeOffset CreatedOn { get; set; }

    /// <summary>D42 stamp — model identifier and prompt version used to generate <see cref="ContentSummary"/>.</summary>
    public SummaryStampDocument? SummaryStamp { get; set; }

    public static class MemoryVersionStatus
    {
        public const string Proposed = "proposed";
        public const string Approved = "approved";
    }

    public static class KindValue
    {
        public const string Decision = "decision";
        public const string Nfr = "nfr";
        public const string Rule = "rule";
        public const string Reference = "reference";
        public const string Retro = "retro";
        public const string Problem = "problem";
        public const string Architecture = "architecture";
        public const string Plan = "plan";
        public const string Backlog = "backlog";
        public const string Preference = "preference";
    }
}
