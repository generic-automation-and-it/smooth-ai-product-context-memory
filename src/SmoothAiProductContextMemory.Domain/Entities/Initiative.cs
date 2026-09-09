namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// A named work stream that classifies memory groups. Registry entity with its own lifecycle
/// state (active/archived), so it keeps a table rather than a column.
/// </summary>
public sealed class Initiative
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = InitiativeStatus.Active;

    public static class InitiativeStatus
    {
        public const string Active = "active";
        public const string Archived = "archived";
    }
}
