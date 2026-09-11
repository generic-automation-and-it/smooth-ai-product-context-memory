using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest;

public class NpgsqlDbErrorMapperTests
{
    private readonly NpgsqlDbErrorMapper _mapper = new();

    [Fact]
    public void Append_only_trigger_maps_to_conflict()
    {
        bool mapped = _mapper.TryMap(
            Wrap(Postgres("Append-only history: UPDATE on memory_version is not permitted", PostgresErrorCodes.RaiseException)),
            out Exception result);

        mapped.ShouldBeTrue();
        result.ShouldBeOfType<ConflictException>();
        result.Message.ShouldBe("History is append-only. Write a new version instead.");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.CheckViolation)]
    [InlineData(PostgresErrorCodes.NotNullViolation)]
    public void Constraint_violations_map_to_conflict(string sqlState)
    {
        bool mapped = _mapper.TryMap(Wrap(Postgres("boom", sqlState)), out Exception result);

        mapped.ShouldBeTrue();
        result.ShouldBeOfType<ConflictException>();
    }

    /// <summary>The provider's own text must never become the caller's problem detail.</summary>
    [Fact]
    public void Mapped_message_does_not_echo_provider_text()
    {
        _mapper.TryMap(
            Wrap(Postgres("duplicate key value violates unique constraint \"IX_memory_uuid\"", PostgresErrorCodes.UniqueViolation)),
            out Exception result);

        result.Message.ShouldNotContain("IX_memory_uuid");
        result.Message.ShouldNotContain("duplicate key");
    }

    /// <summary>Classification is by SQLSTATE, so a message that merely says "unique" is not a conflict.</summary>
    [Fact]
    public void Message_text_alone_is_not_classified()
    {
        bool mapped = _mapper.TryMap(
            new InvalidOperationException("this value must be unique in the caller's own model"),
            out Exception result);

        mapped.ShouldBeFalse();
        result.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Unrecognised_sqlstate_is_left_alone()
    {
        bool mapped = _mapper.TryMap(Wrap(Postgres("connection reset", "08006")), out Exception result);

        mapped.ShouldBeFalse();
        result.ShouldBeOfType<DbUpdateException>();
    }

    [Fact]
    public void Bare_postgres_exception_is_mapped_without_a_wrapper()
    {
        bool mapped = _mapper.TryMap(
            Postgres("Append-only history: DELETE on memory_version is not permitted", PostgresErrorCodes.RaiseException),
            out Exception result);

        mapped.ShouldBeTrue();
        result.ShouldBeOfType<ConflictException>();
    }

    private static PostgresException Postgres(string messageText, string sqlState) =>
        new(messageText, "ERROR", "ERROR", sqlState);

    private static DbUpdateException Wrap(Exception inner) => new("An error occurred.", inner);
}
