using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;

/// <summary>
/// Shared converter factories for JSONB document columns. Serialisation always flows through the
/// typed model, so the <c>v</c> shape marker is stamped in exactly one place (the document base
/// type) and never hand-written. CamelCase matches the on-wire contract, and <see cref="V"/> stays
/// the first key via <c>[JsonPropertyOrder(int.MinValue)]</c>.
/// </summary>
internal static class JsonbConverter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ValueConverter<List<TicketDocument>, string> ForTickets()
        => new(
            value => JsonSerializer.Serialize(value, Options),
            value => JsonSerializer.Deserialize<List<TicketDocument>>(value, Options) ?? new List<TicketDocument>());

    /// <summary>
    /// Snapshot-by-copy comparer for the tickets list. EF's default for a converted reference type
    /// is reference equality with the same instance as snapshot, which makes in-place mutation
    /// (the additive ticket merge) invisible to change detection. <see cref="TicketDocument"/> is a
    /// mutable class without value equality, so elements are compared field-by-field.
    /// </summary>
    public static ValueComparer<List<TicketDocument>> TicketsComparer()
        => new(
            (a, b) => TicketsEqual(a, b),
            v => v.Aggregate(0, (hash, t) => HashCode.Combine(hash, t.Provider, t.Key, t.Url)),
            v => v.Select(t => new TicketDocument { V = t.V, Provider = t.Provider, Key = t.Key, Url = t.Url }).ToList());

    private static bool TicketsEqual(List<TicketDocument>? a, List<TicketDocument>? b)
        => ReferenceEquals(a, b)
            || (a is not null && b is not null && a.Count == b.Count
                && a.Zip(b).All(pair =>
                    pair.First.Provider == pair.Second.Provider
                    && pair.First.Key == pair.Second.Key
                    && pair.First.Url == pair.Second.Url));

    public static ValueConverter<List<SourceDocument>, string> ForSources()
        => new(
            value => JsonSerializer.Serialize(value, Options),
            value => JsonSerializer.Deserialize<List<SourceDocument>>(value, Options) ?? new List<SourceDocument>());

    public static ValueConverter<SummaryStampDocument?, string?> ForSummaryStamp()
        => new(
            value => value == null ? null : JsonSerializer.Serialize(value, Options),
            value => string.IsNullOrWhiteSpace(value)
                ? null
                : JsonSerializer.Deserialize<SummaryStampDocument>(value, Options));
}
