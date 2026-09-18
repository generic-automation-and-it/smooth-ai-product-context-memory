using Microsoft.Extensions.Logging;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Read/reset surface over the recall-feedback set (HLD-004 NFR-03). Never exposes content or query
/// text — identity, count and time only. Capture age for a memory comes from
/// <c>min(memory_version.created_on)</c>, not a column on <c>memory</c>, so the never-recalled query
/// joins the version table rather than a correlated subquery (which dominates the plan).
/// </summary>
public sealed class NpgsqlRecallFeedbackQuery(
    NpgsqlDataSource dataSource,
    ILogger<NpgsqlRecallFeedbackQuery> logger) : IRecallFeedbackQuery
{
    /// <summary>
    /// A memory captured within this many days of the as-of point is treated as "recently captured
    /// and not yet retrieved" and excluded from never-recalled, so a new memory does not pollute the
    /// signal. Matches the placement-evidence harness.
    /// </summary>
    private const int RecentlyCapturedGraceDays = 7;

    private const string Table = "public.recall_feedback";

    public async Task<IReadOnlyList<NeverRecalledRow>> NeverRecalledAsync(
        NeverRecalledRequest request, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);

        string limit = request.Limit is int l ? $" LIMIT {l}" : string.Empty;

        await using var command = new NpgsqlCommand(
            $"""
             SELECT m.uuid, min(mv.created_on) AS captured_on
             FROM memory m
             JOIN memory_version mv ON mv.memory_id = m.id
             WHERE NOT EXISTS (
                 SELECT 1 FROM {Table} rf WHERE rf.memory_uuid = m.uuid)
             GROUP BY m.uuid
             HAVING min(mv.created_on) < @grace_cutoff
             ORDER BY min(mv.created_on)
             {limit}
             """,
            connection);

        command.Parameters.AddWithValue(
            "grace_cutoff", request.AsOf.AddDays(-RecentlyCapturedGraceDays));

        var rows = new List<NeverRecalledRow>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NeverRecalledRow(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }

        return rows;
    }

    public async Task<MissRateResult> MissRateAsync(
        MissRateRequest request, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"""
             SELECT count(DISTINCT retrieval_id) AS retrievals,
                    count(DISTINCT retrieval_id) FILTER (WHERE memory_uuid IS NULL) AS misses
             FROM {Table}
             WHERE occurred_on >= @from AND occurred_on <= @to
             """,
            connection);

        command.Parameters.AddWithValue("from", request.From);
        command.Parameters.AddWithValue("to", request.To);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        int retrievals = reader.GetInt32(0);
        int misses = reader.GetInt32(1);
        double missRate = retrievals == 0 ? 0.0 : (double)misses / retrievals;

        logger.LogDebug(
            "Recall feedback miss rate. Retrievals: {Retrievals} Misses: {Misses}",
            retrievals, misses);

        return new MissRateResult(retrievals, misses, missRate);
    }

    public async Task<int> ResetAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand($"DELETE FROM {Table}", connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
