using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Append-only recall-feedback writer (HLD-004 LADR-02 placement B). One batched multi-row INSERT
/// into <c>recall_feedback</c> on a fresh connection from the shared data source, so it is isolated
/// from the retrieval's unit of work. Fire-and-forget: every failure is swallowed and logged as a
/// constant non-content line — a lost tuning signal is acceptable, a failed recall is not.
/// </summary>
public sealed class NpgsqlRecallFeedback(NpgsqlDataSource dataSource, ILogger<NpgsqlRecallFeedback> logger)
    : IRecallFeedback
{
    public void Record(RecallFeedbackRecord[] records)
    {
        if (records.Length == 0)
        {
            return;
        }

        try
        {
            Write(records);
        }
        catch (Exception ex)
        {
            // No record payload, shape, uuid or query in the log — content must never reach a log line.
            logger.LogDebug(ex, "Recall feedback write failed; retrieval unaffected.");
        }
    }

    private void Write(RecallFeedbackRecord[] records)
    {
        var values = new StringBuilder();
        using var command = new NpgsqlCommand();

        for (int i = 0; i < records.Length; i++)
        {
            RecallFeedbackRecord record = records[i];
            if (i > 0)
            {
                values.Append(", ");
            }

            int b = i * 4;
            values.Append($"(@p{b + 1}, @p{b + 2}, @p{b + 3}, @p{b + 4})");
            command.Parameters.AddWithValue($"p{b + 1}", record.RetrievalId);
            command.Parameters.AddWithValue($"p{b + 2}", (object?)record.MemoryUuid ?? DBNull.Value);
            command.Parameters.AddWithValue($"p{b + 3}", record.Shape);
            command.Parameters.AddWithValue($"p{b + 4}", record.OccurredOn);
        }

        command.CommandText =
            $"INSERT INTO public.recall_feedback (retrieval_id, memory_uuid, shape, occurred_on) VALUES {values}";

        using NpgsqlConnection connection = dataSource.OpenConnection();
        command.Connection = connection;
        command.ExecuteNonQuery();
    }
}
