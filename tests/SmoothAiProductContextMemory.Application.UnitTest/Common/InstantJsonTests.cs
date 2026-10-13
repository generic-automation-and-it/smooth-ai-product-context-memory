using System.Text.Json;
using NodaTime;
using SmoothAiProductContextMemory.Application.Common.Serialization;

namespace SmoothAiProductContextMemory.Application.UnitTest.Common;

public sealed class InstantJsonTests
{
    private static readonly JsonSerializerOptions Json = InstantJson.Configure(new(JsonSerializerDefaults.Web));

    [Theory]
    [InlineData("2026-10-13T16:00:00.123456+02:00", "2026-10-13T14:00:00.123456Z")]
    [InlineData("2026-10-13T09:00:00.123456-05:00", "2026-10-13T14:00:00.123456Z")]
    [InlineData("2026-10-13T14:00:00.123456Z", "2026-10-13T14:00:00.123456Z")]
    public void Wire_and_legacy_json_offsets_become_one_canonical_instant(string input, string expected)
    {
        Instant value = JsonSerializer.Deserialize<Instant>(JsonSerializer.Serialize(input), Json);
        JsonSerializer.Serialize(value, Json).ShouldBe(JsonSerializer.Serialize(expected));
        InstantJson.Parse(input).ShouldBe(value);
    }

    [Theory]
    [InlineData("2026-10-13")]
    [InlineData("2026-10-13T16:00:00")]
    [InlineData("invalid")]
    public void Unzoned_values_are_rejected(string input)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Instant>(JsonSerializer.Serialize(input), Json));
        Should.Throw<FormatException>(() => InstantJson.Parse(input));
    }

    [Fact]
    public void Open_ended_validity_round_trips_as_null()
    {
        JsonSerializer.Deserialize<Instant?>("null", Json).ShouldBeNull();
        JsonSerializer.Serialize<Instant?>(null, Json).ShouldBe("null");
    }
}
