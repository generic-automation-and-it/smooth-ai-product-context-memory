using System.Text;
using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Reads AGE's textual agtype rendering back into JSON: strips the element type annotations that
/// makes <c>nodes(p)</c> / <c>relationships(p)</c> unparseable, and unquotes a scalar rendered via
/// <c>::text</c>.
/// </summary>
internal static class AgtypeArrayReader
{
    /// <summary>
    /// Reads a scalar agtype string rendered via <c>::text</c> and unquotes it. AGE renders a string
    /// value as a quoted JSON literal, so a scalar that is not itself a string reads back unquoted and
    /// is returned as-is, while a string value's surrounding quotes are stripped by deserialising the
    /// quoted span. A quote-led scalar with no interior quote (a raw string literal arising from a
    /// non-quoted column) is returned as-is; one that is quote-led with an interior quote but is not
    /// valid JSON raises <c>JsonException</c>.
    /// </summary>
    internal static string ReadScalar(string raw)
    {
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

    internal static string ReadAgtypeString(NpgsqlDataReader reader, int ordinal) =>
        ReadScalar(reader.GetString(ordinal));

    internal static string ToJson(string agtypeArray)
    {
        var json = new StringBuilder(agtypeArray.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < agtypeArray.Length; i++)
        {
            char c = agtypeArray[i];

            if (escaped)
            {
                json.Append(c);
                escaped = false;
                continue;
            }

            if (inString)
            {
                json.Append(c);
                escaped = c == '\\';
                inString = c != '"';
                continue;
            }

            if (c == '"')
            {
                json.Append(c);
                inString = true;
                continue;
            }

            if (c == ':' && i + 1 < agtypeArray.Length && agtypeArray[i + 1] == ':')
            {
                i = SkipAnnotation(agtypeArray, i + 2) - 1;
                continue;
            }

            json.Append(c);
        }

        return json.ToString();
    }

    private static int SkipAnnotation(string text, int start)
    {
        int i = start;
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
        {
            i++;
        }

        return i;
    }
}
