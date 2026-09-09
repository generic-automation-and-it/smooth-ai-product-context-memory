namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// Append-only history of a group's name and body. Never updated in place — a new description is a
/// new row with a higher <see cref="Version"/>. The database enforces append-only via a trigger.
/// </summary>
public sealed class GroupDescription
{
    public long Id { get; set; }

    public long GroupId { get; set; }

    public MemoryGroup? Group { get; set; }

    public int Version { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedOn { get; set; }
}
