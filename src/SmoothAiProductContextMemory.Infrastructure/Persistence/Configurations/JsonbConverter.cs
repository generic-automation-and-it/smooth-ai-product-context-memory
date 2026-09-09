using System.Text.Json;
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

    public static ValueConverter<List<SourceDocument>, string> ForSources()
        => new(
            value => JsonSerializer.Serialize(value, Options),
            value => JsonSerializer.Deserialize<List<SourceDocument>>(value, Options) ?? new List<SourceDocument>());
}
