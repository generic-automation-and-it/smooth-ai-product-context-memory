using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class AppendOnlyTriggerTests : PersistenceTestBase
{
    public AppendOnlyTriggerTests(AspireFixture aspire) : base(aspire) { }

    private async Task<(MemoryGroup Group, Memory Memory, MemoryVersion V1)> SeedHistoryAsync()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var memory = TestEntities.NewMemory(group.Id, "M", "Subject");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);

        var v1 = TestEntities.NewVersion(memory.Id, 1, "Claim one", isCurrent: true);
        Db.MemoryVersions.Add(v1);

        Db.GroupDescriptions.Add(new GroupDescription
        {
            GroupId = group.Id,
            Version = 1,
            Name = "Group name",
            Body = "Group body",
            CreatedOn = DateTimeOffset.UtcNow,
        });

        await Db.SaveChangesAsync(Ct);
        return (group, memory, v1);
    }

    [Fact]
    public async Task MemoryVersion_Update_Raises()
    {
        var (_, memory, v1) = await SeedHistoryAsync();
        string connStr = Db.Database.GetConnectionString()!;

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE memory_version SET statement = 'changed' WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", v1.Id);

        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        ex.Message.ShouldContain("Append-only history");
    }

    [Fact]
    public async Task MemoryVersion_Delete_Raises()
    {
        var (_, memory, v1) = await SeedHistoryAsync();
        string connStr = Db.Database.GetConnectionString()!;

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM memory_version WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", v1.Id);

        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        ex.Message.ShouldContain("Append-only history");
    }

    [Fact]
    public async Task GroupDescription_Update_Raises()
    {
        var (_, _, _) = await SeedHistoryAsync();
        string connStr = Db.Database.GetConnectionString()!;

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE group_description SET body = 'changed' WHERE version = 1", conn);

        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        ex.Message.ShouldContain("Append-only history");
    }

    [Fact]
    public async Task GroupDescription_Delete_Raises()
    {
        var (_, _, _) = await SeedHistoryAsync();
        string connStr = Db.Database.GetConnectionString()!;

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM group_description WHERE version = 1", conn);

        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        ex.Message.ShouldContain("Append-only history");
    }

    [Fact]
    public async Task CascadeDelete_WithBypass_AllowsHistoryDelete()
    {
        var (group, _, _) = await SeedHistoryAsync();
        string connStr = Db.Database.GetConnectionString()!;

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(Ct);

        // SET LOCAL, not SET: the bypass must auto-revert at COMMIT so it cannot outlive this
        // operation on a pooled connection and hand a later unrelated caller permission to delete
        // history. This is the idiom pinned in PERSISTENCE_AGENTS.md.
        await using var tx = await conn.BeginTransactionAsync(Ct);

        await using (var set = new NpgsqlCommand("SET LOCAL app.allow_history_delete = 'true'", conn, tx))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using (var del = new NpgsqlCommand("DELETE FROM memory_group WHERE id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", group.Id);
            await del.ExecuteNonQueryAsync(Ct);
        }

        await tx.CommitAsync(Ct);

        // With the transaction-scoped bypass, the cascade delete of history is admitted.
        (await Db.MemoryGroups.IgnoreQueryFilters().CountAsync(g => g.Id == group.Id, Ct)).ShouldBe(0);
    }
}
