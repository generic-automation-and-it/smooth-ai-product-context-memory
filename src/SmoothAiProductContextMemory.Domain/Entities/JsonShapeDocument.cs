using System.Text.Json.Serialization;

namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// Base for every JSONB document stored in the database. Each document carries its own <c>v</c>
/// shape marker as the first key, so readers can switch on shape per element. The marker is set in
/// exactly one place — here — and never hand-written. Retrofitting is impossible, so it is
/// mandatory from the first migration.
/// </summary>
public abstract class JsonShapeDocument
{
    public const int CurrentShapeVersion = 1;

    [JsonPropertyOrder(int.MinValue)]
    public int V { get; init; } = CurrentShapeVersion;
}
