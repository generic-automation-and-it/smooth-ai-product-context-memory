namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// Model/prompt stamp (D42) on a <see cref="MemoryVersion"/> so a summary batch can be regenerated.
/// Carries its own <c>v</c> shape marker via <see cref="JsonShapeDocument"/>.
/// </summary>
public sealed class SummaryStampDocument : JsonShapeDocument
{
    public string Model { get; set; } = string.Empty;

    public string PromptVersion { get; set; } = string.Empty;

    public static SummaryStampDocument Create(string model, string promptVersion) => new()
    {
        V = CurrentShapeVersion,
        Model = model,
        PromptVersion = promptVersion,
    };
}
