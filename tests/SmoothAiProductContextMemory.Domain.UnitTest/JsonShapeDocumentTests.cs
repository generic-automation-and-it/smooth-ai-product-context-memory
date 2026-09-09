using System.Text.Json;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Domain.UnitTest;

public class JsonShapeDocumentTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TicketDocument_serialises_with_v_marker_first()
    {
        var doc = TicketDocument.Create("jira", "ACM-1", "https://example.com/ACM-1");
        string json = JsonSerializer.Serialize(doc, Web);

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;

        root.ValueKind.ShouldBe(JsonValueKind.Object);

        string? firstProperty = root.EnumerateObject().First().Name;
        firstProperty.ShouldBe("v");
        root.GetProperty("v").GetInt32().ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        root.GetProperty("provider").GetString().ShouldBe("jira");
        root.GetProperty("key").GetString().ShouldBe("ACM-1");
    }

    [Fact]
    public void SourceDocument_serialises_with_v_marker_first()
    {
        var doc = SourceDocument.Create("ticket", "ACM-1", DateTimeOffset.UtcNow);
        string json = JsonSerializer.Serialize(doc, Web);

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;

        root.EnumerateObject().First().Name.ShouldBe("v");
        root.GetProperty("v").GetInt32().ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        root.GetProperty("kind").GetString().ShouldBe("ticket");
        root.GetProperty("reference").GetString().ShouldBe("ACM-1");
    }

    [Fact]
    public void Factory_methods_stamp_current_shape_version()
    {
        TicketDocument.Create("a", "b", "c").V.ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        SourceDocument.Create("a", "b").V.ShouldBe(JsonShapeDocument.CurrentShapeVersion);
    }

    [Fact]
    public void TicketDocument_round_trips_preserving_v_marker()
    {
        string json = JsonSerializer.Serialize(TicketDocument.Create("jira", "ACM-1", "https://example.com/ACM-1"), Web);
        TicketDocument? back = JsonSerializer.Deserialize<TicketDocument>(json, Web);

        back.ShouldNotBeNull();
        back!.V.ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        back.Provider.ShouldBe("jira");
        back.Key.ShouldBe("ACM-1");
        back.Url.ShouldBe("https://example.com/ACM-1");
    }

    [Fact]
    public void SourceDocument_round_trips_preserving_v_marker()
    {
        string json = JsonSerializer.Serialize(SourceDocument.Create("ticket", "ACM-1", DateTimeOffset.UtcNow), Web);
        SourceDocument? back = JsonSerializer.Deserialize<SourceDocument>(json, Web);

        back.ShouldNotBeNull();
        back!.V.ShouldBe(JsonShapeDocument.CurrentShapeVersion);
        back.Kind.ShouldBe("ticket");
        back.Reference.ShouldBe("ACM-1");
    }
}
