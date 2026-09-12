using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Maps PostgreSQL failures to Application exceptions by SQLSTATE.
/// </summary>
/// <remarks>
/// SQLSTATE, not message text: matching substrings mis-fires on any message that happens to contain
/// a word like "unique", and the text is locale- and version-dependent. The returned messages are
/// fixed strings — provider text never reaches the caller, so a constraint violation cannot leak
/// SQL, column names or values.
/// </remarks>
public sealed class NpgsqlDbErrorMapper : IDbErrorMapper
{
    /// <summary>Prefix raised by the append-only guard trigger.</summary>
    private const string AppendOnlyPrefix = "Append-only history";

    public bool TryMap(Exception exception, out Exception mapped)
    {
        mapped = exception;

        if (Find(exception) is not { } postgres)
        {
            return false;
        }

        mapped = postgres.SqlState switch
        {
            PostgresErrorCodes.RaiseException when postgres.MessageText.StartsWith(AppendOnlyPrefix, StringComparison.Ordinal)
                => new ConflictException("History is append-only. Write a new version instead."),
            PostgresErrorCodes.RaiseException
                => new ConflictException("The request violated a database rule."),
            PostgresErrorCodes.UniqueViolation
                => new ConflictException("A unique constraint was violated."),
            PostgresErrorCodes.ForeignKeyViolation
                => new ConflictException("A referenced record was not found."),
            PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.CheckViolation
                => new ConflictException("The request violated a stored constraint."),
            _ => exception,
        };

        return !ReferenceEquals(mapped, exception);
    }

    private static PostgresException? Find(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
