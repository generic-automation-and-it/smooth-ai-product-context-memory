namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// One ticket reference in a group's <c>tickets</c> JSONB array. Accumulates over time — an epic
/// gains stories across months — so each element is written under whatever shape was current.
/// </summary>
public sealed class TicketDocument : JsonShapeDocument
{
    public string Provider { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public static TicketDocument Create(string provider, string key, string url) => new()
    {
        V = CurrentShapeVersion,
        Provider = provider,
        Key = key,
        Url = url,
    };
}
