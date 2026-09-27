using System.Globalization;
using System.Text;
using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Cypher literal escaping and agtype scalar reading, shared by the graph write path, the
/// traversal read path and the snapshot path.
/// </summary>
/// <remarks>
/// AGE requires <c>cypher()</c>'s map argument to be a prepared-statement parameter of type
/// <c>agtype</c>, which Npgsql cannot bind, so values reach Cypher as literals. Escaping is Cypher's,
/// not SQL's: <c>%L</c>-style doubling produces SQL syntax Cypher does not accept.
/// </remarks>
internal static class CypherLiteral
{
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>
    /// Reads a scalar agtype string rendered via <c>::text</c> and unquotes it. AGE renders a string
    /// value as a quoted JSON literal, so a scalar that is not itself a string reads back quoted; the
    /// surrounding quotes are stripped by deserialising the quoted span. A quote-led scalar with no
    /// interior quote (a raw string literal arising from a non-quoted column) is returned as-is; one
    /// that is quote-led with an interior quote but is not valid JSON raises <c>JsonException</c>.
    /// </summary>
    internal static string ReadAgtypeString(NpgsqlDataReader reader, int ordinal)
    {
        string raw = reader.GetString(ordinal);
        if (raw.Length >= 2 && raw[0] == '"')
        {
            int closingQuote = raw.LastIndexOf('"');
            if (closingQuote > 0)
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw[..(closingQuote + 1)]) ?? string.Empty;
            }
        }

        return raw;
    }

    /// <summary>
    /// Dollar-quotes the Cypher body, choosing a tag the body does not contain so a caller-supplied
    /// reason cannot terminate the literal early.
    /// </summary>
    internal static string DollarWrap(string cypher)
    {
        string tag = "$q$";
        while (cypher.Contains(tag, StringComparison.Ordinal))
        {
            tag = $"$q{Random.Shared.Next():x8}$";
        }

        return $"{tag}\n{cypher}\n{tag}";
    }
}
