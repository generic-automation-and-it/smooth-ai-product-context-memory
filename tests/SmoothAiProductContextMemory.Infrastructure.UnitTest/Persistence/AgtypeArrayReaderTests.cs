using System.Text.Json;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Persistence;

public sealed class AgtypeArrayReaderTests
{
    // Real AGE 1.7 renderings, captured from nodes(p) / relationships(p) on the pinned image. The
    // annotation suffix is what makes the payload unparseable; the reason strings are the values a
    // caller actually writes.
    private const string NodesAgtype =
        """[[{"id": 844424930131969, "label": "Memory", "properties": {"memory_uuid": "11111111-1111-1111-1111-111111111111"}}::vertex, {"id": 844424930131970, "label": "Memory", "properties": {"memory_uuid": "22222222-2222-2222-2222-222222222222"}}::vertex]]""";

    private const string QuoteLedHopsAgtype =
        """[[{"id": 1125899906842626, "label": "LINKS", "end_id": 844424930131970, "start_id": 844424930131969, "properties": {"reason": "\"Cited\" from ADR-3, not paraphrased", "relation": "Cited from \"ADR-3\" verbatim"}}::edge]]""";

    [Fact]
    public void ToJson_strips_the_element_type_annotations()
    {
        // Without the strip the payload is not JSON at all, so this asserts parseability rather than
        // a substring removal.
        using JsonDocument parsed = JsonDocument.Parse(AgtypeArrayReader.ToJson(NodesAgtype));
        parsed.RootElement[0].GetArrayLength().ShouldBe(2);
        parsed.RootElement[0][0].GetProperty("label").GetString().ShouldBe("Memory");
        AgtypeArrayReader.ToJson(NodesAgtype).ShouldNotContain("::vertex");
    }

    [Fact]
    public void ToJson_preserves_a_quote_led_reason_exactly()
    {
        // The array path and the scalar path both carry a caller-written reason. This pins the array
        // path against being "fixed" the way the scalar path was.
        using JsonDocument parsed = JsonDocument.Parse(AgtypeArrayReader.ToJson(QuoteLedHopsAgtype));
        string reason = parsed.RootElement[0][0].GetProperty("properties").GetProperty("reason").GetString()!;

        reason.ShouldBe("\"Cited\" from ADR-3, not paraphrased");
        reason.Length.ShouldBe(35);
    }

    [Fact]
    public void ToJson_preserves_a_colon_inside_a_string()
    {
        // Annotation stripping keys on "::" outside a string only, so a value containing a colon pair
        // is content and must survive.
        const string agtype =
            """[[{"id": 1, "label": "LINKS", "properties": {"reason": "see ADR::3 for the quote"}}::edge]]""";

        using JsonDocument parsed = JsonDocument.Parse(AgtypeArrayReader.ToJson(agtype));

        parsed.RootElement[0][0].GetProperty("properties").GetProperty("reason").GetString()
            .ShouldBe("see ADR::3 for the quote");
    }

    [Fact]
    public void ToJson_preserves_an_escaped_quote()
    {
        const string agtype =
            """[[{"id": 1, "label": "LINKS", "properties": {"reason": "a \"b\" c"}}::edge]]""";

        using JsonDocument parsed = JsonDocument.Parse(AgtypeArrayReader.ToJson(agtype));

        parsed.RootElement[0][0].GetProperty("properties").GetProperty("reason").GetString()
            .ShouldBe("a \"b\" c");
    }
}
