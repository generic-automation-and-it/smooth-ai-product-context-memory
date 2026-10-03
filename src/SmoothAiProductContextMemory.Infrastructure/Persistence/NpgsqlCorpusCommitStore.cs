using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

public sealed class NpgsqlCorpusCommitStore(SmoothAiProductContextMemoryDbContext db) : ICorpusCommitStore
{
    public Task<CorpusState> GetStateAsync(CancellationToken cancellationToken) => ReadStateAsync(false, cancellationToken);

    public Task<CorpusState> LockAsync(CancellationToken cancellationToken)
    {
        RequireTransaction();
        return ReadStateAsync(true, cancellationToken);
    }

    private async Task<CorpusState> ReadStateAsync(bool locked, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = Command("SELECT epoch, revision FROM public.corpus_state WHERE singleton = true" + (locked ? " FOR UPDATE" : string.Empty));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Corpus state is missing; migrate the database first.");
        }

        return new CorpusState(reader.GetGuid(0), reader.GetInt64(1));
    }

    public async Task<OperationReceipt?> FindAsync(string operationKey, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = Command("SELECT operation_key, payload_hash, operation_type, result::text, committed_at FROM public.operation_receipt WHERE operation_key = @key");
        command.Parameters.AddWithValue("key", operationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OperationReceipt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    public async Task RecordAsync(OperationReceipt receipt, CancellationToken cancellationToken)
    {
        RequireTransaction();
        await using var command = Command("INSERT INTO public.operation_receipt (operation_key, payload_hash, operation_type, result, committed_at) VALUES (@key, @hash, @type, @result, @at)");
        command.Parameters.AddWithValue("key", receipt.OperationKey);
        command.Parameters.AddWithValue("hash", receipt.PayloadHash);
        command.Parameters.AddWithValue("type", receipt.OperationType);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, receipt.ResultJson);
        command.Parameters.AddWithValue("at", receipt.CommittedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task AdvanceRevisionAsync(CancellationToken cancellationToken)
    {
        RequireTransaction();
        await db.Database.ExecuteSqlRawAsync("UPDATE public.corpus_state SET revision = revision + 1 WHERE singleton", cancellationToken);
    }

    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
        db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Corpus commit operations require the corpus transaction.");
        }
    }
}
