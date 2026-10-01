namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Normalises a caller-supplied instant before it is bound as a <c>timestamptz</c> parameter.
/// </summary>
/// <remarks>
/// Npgsql refuses to write a <see cref="DateTimeOffset"/> with a non-zero offset to
/// <c>timestamptz</c>, and a wire <c>asOf</c> keeps whatever offset the caller sent. The conversion
/// lives at the providers rather than in the Application validators because the constraint is the
/// driver's: every caller of <c>IMemorySearch</c> and <c>IMemoryTraversal</c>, in-process ones
/// included, reaches the bind through here. The instant is unchanged; only its representation is.
/// </remarks>
internal static class PostgresInstant
{
    internal static DateTimeOffset? ToUtc(DateTimeOffset? value) => value?.ToUniversalTime();
}
