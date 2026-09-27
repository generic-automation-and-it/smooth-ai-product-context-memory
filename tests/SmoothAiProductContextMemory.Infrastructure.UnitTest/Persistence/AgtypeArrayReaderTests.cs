using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Persistence;

public sealed class AgtypeArrayReaderTests
{
    [Fact]
    public void ReadScalar_unquotes_a_quoted_json_string() =>
        AgtypeArrayReader.ReadScalar("\"hello\"").ShouldBe("hello");

    [Fact]
    public void ReadScalar_unquotes_a_string_containing_an_escaped_quote() =>
        AgtypeArrayReader.ReadScalar("\"a\\\"b\"").ShouldBe("a\"b");

    [Fact]
    public void ReadScalar_returns_a_non_string_scalar_as_is()
    {
        // AGE renders a non-string scalar (number, boolean) unquoted via ::text.
        AgtypeArrayReader.ReadScalar("123").ShouldBe("123");
        AgtypeArrayReader.ReadScalar("true").ShouldBe("true");
    }

    [Fact]
    public void ReadScalar_returns_a_quote_prefixed_non_json_string_as_is()
    {
        // A scalar that starts with a quote but is not a JSON string (a raw literal from a
        // non-quoted column) has no closing quoted span to strip, so it is returned untouched.
        AgtypeArrayReader.ReadScalar("\"not a json string").ShouldBe("\"not a json string");
    }

    [Fact]
    public void ReadScalar_throws_on_quote_led_value_with_an_interior_quote_that_is_not_valid_json() =>
        Should.Throw<System.Text.Json.JsonException>(() => AgtypeArrayReader.ReadScalar("\"a\"b\""));
}
