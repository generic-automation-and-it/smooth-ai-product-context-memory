using System.Text;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Turns AGE's textual rendering of an agtype list of vertices or edges into parseable JSON.
/// </summary>
/// <remarks>
/// AGE renders each element of <c>nodes(p)</c> / <c>relationships(p)</c> with a trailing type
/// annotation — <c>{…}::vertex</c>, <c>{…}::edge</c> — which no JSON parser accepts. Blind text
/// replacement would also corrupt any property value that happens to contain the same characters, and
/// <c>reason</c> is caller-supplied free text, so the annotation is removed by scanning: string
/// literals are skipped, and only an annotation sitting between a closing brace and the next element
/// boundary is dropped.
/// </remarks>
internal static class AgtypeArrayReader
{
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
