using System.Globalization;
using System.Text;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Cypher literal escaping, shared by the relationship write path and the traversal read path.
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
