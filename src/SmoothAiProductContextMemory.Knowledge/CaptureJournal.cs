using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace SmoothAiProductContextMemory.Knowledge;

public sealed class CaptureJournal
{
    private readonly string connectionString;
    private readonly long maxBytes;
    private readonly string databasePath;
    private const long JobAllowance = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1);
    public CaptureJournal(string path, long maxBytes = 128 * 1024 * 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        this.maxBytes = maxBytes;
        databasePath = Path.GetFullPath(path);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS captures(id TEXT PRIMARY KEY, request_key TEXT UNIQUE NOT NULL, hash TEXT NOT NULL, status TEXT NOT NULL, updated TEXT NOT NULL, body TEXT NOT NULL); CREATE TABLE IF NOT EXISTS clarifications(request_key TEXT PRIMARY KEY, hash TEXT NOT NULL, capture_id TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<CaptureJob> AcceptAsync(CaptureRequest input, string rawHash, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = "SELECT hash,body FROM captures WHERE request_key=$key";
            find.Parameters.AddWithValue("$key", Hash(input.IdempotencyKey));
            using (var reader = find.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != rawHash) throw new KnowledgeException("idempotency_conflict");
                    return KnowledgeJson.Deserialize<CaptureJob>(reader.GetString(1));
                }
            }
            var job = new CaptureJob(Guid.NewGuid(), rawHash, input, Workflow.Version, "received", BudgetLedger.Start(new BudgetLimits()), null, null, null, null, null, [], [], [], [], null, DateTimeOffset.UtcNow);
            string body = KnowledgeJson.Serialize(job);
            EnsureCapacity(connection, transaction, JobAllowance);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO captures VALUES($id,$key,$hash,$status,$updated,$body)";
            insert.Parameters.AddWithValue("$id", job.Id.ToString());
            insert.Parameters.AddWithValue("$key", Hash(input.IdempotencyKey));
            insert.Parameters.AddWithValue("$hash", rawHash);
            insert.Parameters.AddWithValue("$status", job.Status);
            insert.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
            transaction.Commit();
            return job;
        }
        finally { gate.Release(); }
    }

    public CaptureJob? Get(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM captures WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is string body ? KnowledgeJson.Deserialize<CaptureJob>(body) : null;
    }

    public IReadOnlyList<Guid> Pending()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM captures WHERE status IN ('received','processing') ORDER BY updated LIMIT 100";
        using var reader = command.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    public async Task SaveAsync(CaptureJob job, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var capacity = connection.CreateCommand();
            capacity.CommandText = "SELECT length(body) FROM captures WHERE id=$id";
            capacity.Parameters.AddWithValue("$id", job.Id.ToString());
            long oldBytes = Convert.ToInt64(capacity.ExecuteScalar()) * 4L;
            string body = KnowledgeJson.Serialize(job);
            if (Encoding.UTF8.GetByteCount(body) > JobAllowance) throw new KnowledgeException("job_storage_limit");
            using var transaction = connection.BeginTransaction();
            EnsureCapacity(connection, transaction, Math.Max(0, body.Length * 4L - oldBytes));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE captures SET status=$status,updated=$updated,body=$body WHERE id=$id";
            command.Parameters.AddWithValue("$id", job.Id.ToString());
            command.Parameters.AddWithValue("$status", job.Status);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$body", body);
            if (command.ExecuteNonQuery() != 1) throw new KnowledgeException("capture_not_found");
            transaction.Commit();
        }
        finally { gate.Release(); }
    }

    public async Task<CaptureJob> ClarifyAsync(Guid id, ClarificationRequest request, string rawHash, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT hash,capture_id FROM clarifications WHERE request_key=$key";
            check.Parameters.AddWithValue("$key", Hash(request.IdempotencyKey));
            using (var reader = check.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != rawHash || reader.GetString(1) != id.ToString()) throw new KnowledgeException("idempotency_conflict");
                    return Get(id) ?? throw new KnowledgeException("capture_not_found");
                }
            }
            var job = Get(id) ?? throw new KnowledgeException("capture_not_found");
            if (job.Status is "processing" or "received") throw new KnowledgeException("capture_busy");
            if (job.ErrorClass is "corpus_epoch_changed" or "workflow_version_changed") throw new KnowledgeException("reconciliation_required");
            if (job.Input is null) throw new KnowledgeException("handoff_purged");
            var messages = job.Input.Messages.Concat(request.Messages).ToArray();
            if (messages.Select(m => m.Id).Distinct().Count() != messages.Length) throw new KnowledgeException("duplicate_message_id");
            var limits = job.Budget.Limits;
            if (request.AdditionalBudget is { } extra)
            {
                if (extra.Calls is < 0 or > 12 || extra.InputTokens is < 0 or > 100000 || extra.OutputTokens is < 0 or > 12000 || extra.Dollars is < 0 or > 2 || extra.DeadlineSeconds is < 1 or > 120) throw new KnowledgeException("invalid_additional_budget");
                limits = new BudgetLimits(limits.Calls + extra.Calls, limits.InputTokens + extra.InputTokens, limits.OutputTokens + extra.OutputTokens, limits.Dollars + extra.Dollars, extra.DeadlineSeconds);
            }
            bool newEvidence = request.Messages.Count > 0 || request.Selectors is not null;
            job = job with { Input = job.Input with { Messages = messages, Selectors = request.Selectors ?? job.Input.Selectors }, GroupUuid = request.Selectors is null ? job.GroupUuid : null, PendingGroupOperation = request.Selectors is null ? job.PendingGroupOperation : null, Status = "received", Budget = job.Budget with { Limits = limits }, DeadlineUtc = ExtendDeadline(job.DeadlineUtc, request.AdditionalBudget), Plan = newEvidence ? null : job.Plan, Candidates = newEvidence ? null : job.Candidates, ComparisonIndex = newEvidence ? 0 : job.ComparisonIndex, Deferred = newEvidence ? [] : job.Deferred, Questions = newEvidence ? [] : job.Questions, ErrorClass = null };
            string body = KnowledgeJson.Serialize(job);
            EnsureCapacity(connection, transaction, JobAllowance);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO clarifications VALUES($key,$hash,$id); UPDATE captures SET status='received',body=$body WHERE id=$id";
            command.Parameters.AddWithValue("$key", Hash(request.IdempotencyKey));
            command.Parameters.AddWithValue("$hash", rawHash);
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$body", body);
            command.ExecuteNonQuery();
            transaction.Commit();
            return job;
        }
        finally { gate.Release(); }
    }

    public async Task<CaptureJob> ReconcileAsync(Guid id, ReconciliationRequest request, string rawHash, CorpusState current, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT hash,capture_id FROM clarifications WHERE request_key=$key";
            check.Parameters.AddWithValue("$key", Hash("reconcile:" + request.IdempotencyKey));
            using (var reader = check.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != rawHash || reader.GetString(1) != id.ToString()) throw new KnowledgeException("idempotency_conflict");
                    return Get(id) ?? throw new KnowledgeException("capture_not_found");
                }
            }
            var job = Get(id) ?? throw new KnowledgeException("capture_not_found");
            if (!request.AcknowledgeRestore) throw new KnowledgeException("restore_acknowledgement_required");
            if (job.Input is null) throw new KnowledgeException("handoff_purged");
            if (job.Status is "received" or "processing") throw new KnowledgeException("capture_busy");
            if (job.Corpus is null || job.Corpus.Epoch != request.PreviousCorpusEpoch || current.Epoch != request.ExpectedCorpusEpoch) throw new KnowledgeException("reconciliation_epoch_mismatch");
            if (job.Corpus.Epoch == current.Epoch && job.ErrorClass != "corpus_epoch_changed") throw new KnowledgeException("restore_not_detected");
            if ((job.Reconciliations?.Count ?? 0) >= 10) throw new KnowledgeException("reconciliation_history_limit");
            var limits = job.Budget.Limits;
            if (request.AdditionalBudget is { } extra)
            {
                if (extra.Calls is < 0 or > 12 || extra.InputTokens is < 0 or > 100000 || extra.OutputTokens is < 0 or > 12000 || extra.Dollars is < 0 or > 2 || extra.DeadlineSeconds is < 1 or > 120) throw new KnowledgeException("invalid_additional_budget");
                limits = new BudgetLimits(limits.Calls + extra.Calls, limits.InputTokens + extra.InputTokens, limits.OutputTokens + extra.OutputTokens, limits.Dollars + extra.Dollars, extra.DeadlineSeconds);
            }
            int generation = job.Generation + 1;
            var summary = new ReconciliationSummary(generation, job.Corpus.Epoch, current.Epoch, job.Committed.Select(batch => batch.OperationKey).ToArray(), job.PendingCommit?.OperationKey ?? job.PendingGroupOperation, job.Plan is null ? null : Hash(KnowledgeJson.Serialize(job.Plan)), job.Budget.Usage with { OutstandingReservations = job.Budget.Reservations.Count }, DateTimeOffset.UtcNow, job.PendingCommit is null ? null : Hash(KnowledgeJson.Serialize(job.PendingCommit)));
            var audit = new ReconciliationAudit(summary, job.Committed, job.Plan, job.PendingCommit, job.PendingGroupOperation);
            job = job with { Status = "received", Corpus = current, GroupUuid = null, Plan = null, PendingCommit = null, PendingGroupOperation = null, Committed = [], Candidates = null, ComparisonIndex = 0, Generation = generation, Reconciliations = [.. job.Reconciliations ?? [], audit], Deferred = [], Questions = [], ErrorClass = null, Budget = job.Budget with { Limits = limits }, DeadlineUtc = ExtendDeadline(job.DeadlineUtc, request.AdditionalBudget), SourceNamespace = job.SourceNamespace ?? job.Input.SourceNamespace ?? job.Id.ToString(), WorkflowVersion = Workflow.Version };
            string body = KnowledgeJson.Serialize(job);
            if (Encoding.UTF8.GetByteCount(body) > JobAllowance) throw new KnowledgeException("job_storage_limit");
            EnsureCapacity(connection, transaction, Encoding.UTF8.GetByteCount(body));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO clarifications VALUES($key,$hash,$id); UPDATE captures SET status='received',updated=$updated,body=$body WHERE id=$id";
            command.Parameters.AddWithValue("$key", Hash("reconcile:" + request.IdempotencyKey));
            command.Parameters.AddWithValue("$hash", rawHash);
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$body", body);
            command.ExecuteNonQuery();
            transaction.Commit();
            return job;
        }
        finally { gate.Release(); }
    }

    public async Task PurgeAsync(TimeSpan retention, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT body FROM captures WHERE status='processed' AND updated < $cutoff LIMIT 100";
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O"));
            var jobs = new List<CaptureJob>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) jobs.Add(KnowledgeJson.Deserialize<CaptureJob>(reader.GetString(0)));
            foreach (var job in jobs.Where(job => job.Input is not null))
            {
                var compact = job with { Input = null, Candidates = null, LastExtraction = null, PendingCommit = null, Plan = (job.Plan ?? []).Select(Compact).ToArray(), Reconciliations = job.Reconciliations?.Select(audit => audit with { PreviousPendingCommit = null, PreviousPlan = audit.PreviousPlan?.Select(Compact).ToArray() }).ToArray() };
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE captures SET body=$body WHERE id=$id AND status='processed'";
                update.Parameters.AddWithValue("$id", job.Id.ToString());
                update.Parameters.AddWithValue("$body", KnowledgeJson.Serialize(compact));
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }
        finally { gate.Release(); }
    }

    public void Backup(string destination)
    {
        using var connection = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open();
        connection.BackupDatabase(target);
    }

    private static Change Compact(Change change) => change with { Candidate = change.Candidate with { Content = "", AuthorityQuote = null }, Target = change.Target is null ? null : change.Target with { Body = "", Sources = change.Target.Sources.Select(source => source with { Evidence = source.Evidence is null ? null : source.Evidence with { AuthorityQuote = null } }).ToArray() } };
    private static DateTimeOffset? ExtendDeadline(DateTimeOffset? current, BudgetLimits? additional) => additional is null ? current : (current is { } deadline && deadline > DateTimeOffset.UtcNow ? deadline : DateTimeOffset.UtcNow).AddSeconds(additional.DeadlineSeconds);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA wal_autocheckpoint=100";
        command.ExecuteNonQuery();
        return connection;
    }
    private void EnsureCapacity(SqliteConnection connection, SqliteTransaction transaction, long extra)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(SUM(CASE WHEN status IN ('received','processing','partial','needs_input') THEN max(length(body)*4,4194304) ELSE length(body)*4 END),0) FROM captures";
        long allocated = Convert.ToInt64(command.ExecuteScalar());
        long physical = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
        if (Math.Max(allocated, physical) + extra > maxBytes * 8 / 10) throw new KnowledgeException("journal_full");
    }
}
