namespace SmoothAiProductContextMemory.Domain;

/// <summary>
/// Well-known relationship names. Open vocabulary — these are usage names, not a closed set.
/// Unknown relations still persist as <c>:LINKS.relation</c>.
/// </summary>
public static class MemoryRelation
{
    public const string DependsOn = "depends_on";

    public const string RelatesTo = "relates_to";

    public const string Contradicts = "contradicts";

    public const string Supersedes = "supersedes";

    public const string Implements = "implements";
}
