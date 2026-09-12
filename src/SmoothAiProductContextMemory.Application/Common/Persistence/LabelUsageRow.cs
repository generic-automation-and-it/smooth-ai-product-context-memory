namespace SmoothAiProductContextMemory.Application.Common.Persistence;

public sealed class LabelUsageRow
{
    public string Name { get; set; } = string.Empty;

    public long Uses { get; set; }
}
