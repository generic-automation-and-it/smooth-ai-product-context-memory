using System.Text;
using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Reads AGE's textual agtype rendering back into JSON: strips the element type annotations that
/// makes <c>nodes(p)</c> / <c>relationships(p)</c> unparseable.
/// </summary>
internal static class AgtypeArrayReader
{
    /// <summary>
    /// Reads a scalar agtype string rendered via <c>::text</c>. The cast already yields the value
    /// itself, so this is the identity and there is nothing to parse.
    /// </summary>
    /// <remarks>
    /// This deliberately has no unquoting step. The premise such a step rested on — that AGE renders a
    /// string value as a quoted JSON literal — does not hold: a <c>::text</c> cast of a string property
    /// returns it unquoted, with interior quotes intact. A quote-led value is therefore <em>data</em>,
    /// not encoding, and stripping it destroys content. Measured on the pinned AGE 1.7: a reason of
    /// <c>"Cited" from ADR-3, not paraphrased</c> is delivered as those 35 characters and was being
    /// reduced to <c>Cited</c>; a value with a second quoted run raised <c>JsonException</c>. The
    /// ticket-graph reader has always read this cast raw, which is the correct treatment — the two
    /// readers of one column are now the same read.
    /// </remarks>
    internal static string ReadAgtypeString(NpgsqlDataReader reader, int ordinal) =>
        reader.GetString(ordinal);

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
