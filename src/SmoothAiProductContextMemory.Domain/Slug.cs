using System.Globalization;
using System.Text;

namespace SmoothAiProductContextMemory.Domain;

/// <summary>
/// Normalises a subject description into a stable slug. Application-computed (not a database
/// generated column) so the logic lives in exactly one place, in the layer that owns the domain.
/// The result is what the soft unique (group_id, subject_slug) backstop matches on.
/// </summary>
public static class Slug
{
    public static string Subject(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        string normalised = description.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);

        foreach (char c in normalised)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        string deaccented = builder.ToString().Normalize(NormalizationForm.FormC);

        var slug = new StringBuilder(deaccented.Length);
        bool lastWasSeparator = false;

        foreach (char c in deaccented)
        {
            if (char.IsLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && slug.Length > 0)
            {
                slug.Append('-');
                lastWasSeparator = true;
            }
        }

        return slug.ToString().Trim('-');
    }
}
