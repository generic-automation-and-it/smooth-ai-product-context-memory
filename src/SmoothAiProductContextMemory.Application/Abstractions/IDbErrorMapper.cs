namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Translates database-provider failures into the Application's own exception vocabulary.
/// </summary>
/// <remarks>
/// Provider error identity (SQLSTATE) lives in Infrastructure — matching on substrings of an
/// exception message is brittle and mis-fires on any message that happens to contain the word
/// "unique". Implementations must never surface SQL or provider text to the caller.
/// </remarks>
public interface IDbErrorMapper
{
    /// <summary>
    /// Maps a provider failure to a domain-meaningful exception. Returns <see langword="false"/>
    /// when the exception is not a recognised database constraint or trigger violation, in which
    /// case <paramref name="mapped"/> is the original exception.
    /// </summary>
    bool TryMap(Exception exception, out Exception mapped);
}
