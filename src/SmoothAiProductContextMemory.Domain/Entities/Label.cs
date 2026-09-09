namespace SmoothAiProductContextMemory.Domain.Entities;

/// <summary>
/// Advisory facet registry. Has its own lifecycle state (active/draft/deleted). Deliberately
/// non-enforcing: the capturing skill may propose new vocabulary, so there is no FK from the
/// facets array on <see cref="Memory"/> to this table.
/// </summary>
public sealed class Label
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = LabelStatus.Active;

    public static class LabelStatus
    {
        public const string Active = "active";
        public const string Draft = "draft";
        public const string Deleted = "deleted";
    }
}
