namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// One provenance record in a <see cref="MemoryVersion"/>'s <c>sources</c> JSONB array. Sits on the
/// version because provenance records where *this claim* came from. Written once with its version;
/// each element carries its own <c>v</c> shape marker.
/// </summary>
public sealed class SourceDocument : JsonShapeDocument
{
    public string Kind { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    public DateTimeOffset? CapturedAt { get; set; }

    public static SourceDocument Create(string kind, string reference, DateTimeOffset? capturedAt = null) => new()
    {
        V = CurrentShapeVersion,
        Kind = kind,
        Reference = reference,
        CapturedAt = capturedAt,
    };
}
