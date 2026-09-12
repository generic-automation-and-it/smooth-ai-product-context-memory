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
    /// <summary>
    /// Normalises <paramref name="description"/> into a subject slug.
    /// </summary>
    /// <exception cref="ArgumentNullException">The description is null.</exception>
    /// <exception cref="ArgumentException">
    /// The description contains no letters or digits, which would produce an empty slug. Empty slugs
    /// are rejected rather than stored: every such description would collapse to the same value and
    /// collide on the unique (group_id, subject_slug) backstop, so two unrelated subjects would be
    /// treated as duplicates of one another.
    /// </exception>
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

        string result = slug.ToString().Trim('-');

        if (result.Length == 0)
        {
            throw new ArgumentException(
                "Description contains no letters or digits; subject slug would be empty.",
                nameof(description));
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="description"/> yields a non-empty subject slug; false when it would
    /// produce an empty slug (no letters or digits). Lets a validator reject the input as a 400
    /// instead of letting <see cref="Subject"/> throw an unmapped <see cref="ArgumentException"/>.
    /// </summary>
    public static bool TrySubject(string? description, out string? slug)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            slug = null;
            return false;
        }

        try
        {
            slug = Subject(description);
            return true;
        }
        catch (ArgumentException)
        {
            slug = null;
            return false;
        }
    }
}
