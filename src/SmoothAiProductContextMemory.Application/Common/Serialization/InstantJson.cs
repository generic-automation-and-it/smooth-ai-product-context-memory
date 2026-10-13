using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using NodaTime.Text;

namespace SmoothAiProductContextMemory.Application.Common.Serialization;

/// <summary>One absolute-time wire contract for HTTP, JSONB and portable snapshots.</summary>
public static class InstantJson
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.Converters.Insert(0, new InstantConverter());
        return options;
    }

    public static Instant Parse(string value)
    {
        var utc = InstantPattern.ExtendedIso.Parse(value);
        if (utc.Success) return utc.Value;
        var offset = OffsetDateTimePattern.ExtendedIso.Parse(value);
        if (offset.Success) return offset.Value.ToInstant();
        throw new FormatException("Timestamp must be ISO-8601 with Z or an explicit UTC offset.");
    }

    private sealed class InstantConverter : JsonConverter<Instant>
    {
        public override Instant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Timestamp must be an ISO-8601 string with Z or an explicit UTC offset.");
            try { return Parse(reader.GetString()!); }
            catch (FormatException)
            {
                throw new JsonException("Timestamp must be ISO-8601 with Z or an explicit UTC offset.");
            }
        }

        public override void Write(Utf8JsonWriter writer, Instant value, JsonSerializerOptions options) =>
            writer.WriteStringValue(InstantPattern.ExtendedIso.Format(value));
    }
}
