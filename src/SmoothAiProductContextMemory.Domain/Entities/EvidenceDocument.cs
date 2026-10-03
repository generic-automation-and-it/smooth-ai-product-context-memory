namespace SmoothAiProductContextMemory.Domain.Entities;

public sealed class EvidenceDocument : JsonShapeDocument
{
    public string Category { get; init; } = "unknown";
    public string? Applicability { get; init; }
    public string? Authority { get; init; }
    public string? AuthorityReference { get; init; }
    public string? AuthorityQuote { get; init; }
    public string? ScopeDimension { get; init; }
    public string? ScopeIdentifier { get; init; }
}
