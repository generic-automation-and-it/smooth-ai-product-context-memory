using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// AGE access for the product-name registry (HLD-003 LADR-10). Products are graph-native; the
/// <c>Initiative</c> and <c>Repo</c> vertices are identity-only and MERGEd on demand, matching the
/// LADR-08 exception for <c>Ticket</c>. Uniqueness the graph cannot express (one product per name with
/// case-folded collisions refused, one incoming <c>HAS_REPO</c> per repo, one edge per pair) is
/// enforced here under a transaction advisory lock.
/// </summary>
public sealed class NpgsqlProductGraph(SmoothAiProductContextMemoryDbContext db) : IProductGraph
{
    private const int AdvisoryLockKey = 734921;
    private const int AdvisoryLockId = 3;

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        string cypher = $"""
            MATCH (p:{AgeSession.ProductLabel})
            WHERE p.name = {Quote(name)}
            RETURN count(p)
            """;
        string? count = await ExecuteScalarAsync(cypher, cancellationToken);
        return ParseAgtypeInteger(count) > 0;
    }

    public async Task<bool> UpsertAsync(
        string name,
        string? description,
        string? status,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(
            ct => UpsertWithinTransactionAsync(name, description, status, ct),
            cancellationToken);

    public async Task<IReadOnlyList<ProductRegistryRow>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = new List<ProductRegistryRow>();
        await using (NpgsqlDataReader productReader = await ExecuteReaderAsync(
            $"""
            MATCH (p:{AgeSession.ProductLabel})
            RETURN p.name, p.description, p.status
            """,
            "(name agtype, description agtype, status agtype)",
            cancellationToken))
        {
            while (await productReader.ReadAsync(cancellationToken))
            {
                rows.Add(new ProductRegistryRow(
                    ReadString(productReader, 0),
                    ReadNullableString(productReader, 1),
                    ReadString(productReader, 2),
                    0,
                    0));
            }
        }

        IReadOnlyDictionary<string, int> initiativeCounts = await CountHopsAsync(
            AgeSession.HasInitiativeEdgeLabel,
            cancellationToken);
        IReadOnlyDictionary<string, int> repoCounts = await CountHopsAsync(
            AgeSession.HasRepoEdgeLabel,
            cancellationToken);

        return [.. rows.Select(r => r with { InitiativeCount = initiativeCounts.GetValueOrDefault(r.Name), RepoCount = repoCounts.GetValueOrDefault(r.Name) })];
    }

    public async Task<ProductRegistryRow?> GetAsync(string name, CancellationToken cancellationToken)
    {
        string currentName;
        string? description;
        string status;
        await using (NpgsqlDataReader reader = await ExecuteReaderAsync(
            $"""
            MATCH (p:{AgeSession.ProductLabel})
            WHERE p.name = {Quote(name)}
            RETURN p.name, p.description, p.status
            """,
            "(name agtype, description agtype, status agtype)",
            cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            currentName = ReadString(reader, 0);
            description = ReadNullableString(reader, 1);
            status = ReadString(reader, 2);
        }

        int initiativeCount = await CountHopsForProductAsync(name, AgeSession.HasInitiativeEdgeLabel, cancellationToken);
        int repoCount = await CountHopsForProductAsync(name, AgeSession.HasRepoEdgeLabel, cancellationToken);
        return new ProductRegistryRow(currentName, description, status, initiativeCount, repoCount);
    }

    public async Task<bool> LinkInitiativeAsync(
        string productName,
        string initiativeName,
        string reason,
        string source,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(
            ct => LinkInitiativeWithinTransactionAsync(productName, initiativeName, reason, source, ct),
            cancellationToken);

    public async Task<bool> UnlinkInitiativeAsync(
        string productName,
        string initiativeName,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(
            ct => UnlinkInitiativeWithinTransactionAsync(productName, initiativeName, ct),
            cancellationToken);

    public async Task<RepoLinkResult> LinkRepoAsync(
        string productName,
        string repoName,
        string reason,
        string source,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(
            ct => LinkRepoWithinTransactionAsync(productName, repoName, reason, source, ct),
            cancellationToken);

    public async Task<IReadOnlyList<string>> GetInitiativeNamesAsync(string productName, CancellationToken cancellationToken) =>
        await ReadColumnAsync(
            $"MATCH (p:{AgeSession.ProductLabel})-[e:{AgeSession.HasInitiativeEdgeLabel}]->(i:{AgeSession.InitiativeLabel}) WHERE p.name = {Quote(productName)} RETURN i.name",
            "name",
            cancellationToken);

    public async Task<IReadOnlyList<string>> GetRepoNamesAsync(string productName, CancellationToken cancellationToken) =>
        await ReadColumnAsync(
            $"MATCH (p:{AgeSession.ProductLabel})-[e:{AgeSession.HasRepoEdgeLabel}]->(r:{AgeSession.RepoLabel}) WHERE p.name = {Quote(productName)} RETURN r.name",
            "name",
            cancellationToken);

    public async Task<IReadOnlyList<string>> GetProductsForInitiativeAsync(string initiativeName, CancellationToken cancellationToken) =>
        await ReadColumnAsync(
            $"MATCH (p:{AgeSession.ProductLabel})-[e:{AgeSession.HasInitiativeEdgeLabel}]->(i:{AgeSession.InitiativeLabel}) WHERE i.name = {Quote(initiativeName)} RETURN p.name",
            "name",
            cancellationToken);

    public async Task<string?> GetProductForRepoAsync(string repoName, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> owners = await ReadColumnAsync(
            $"MATCH (r:{AgeSession.RepoLabel})<-[e:{AgeSession.HasRepoEdgeLabel}]-(p:{AgeSession.ProductLabel}) WHERE r.name = {Quote(repoName)} RETURN p.name",
            "name",
            cancellationToken);
        return owners.Count > 0 ? owners[0] : null;
    }

    public async Task<IReadOnlyDictionary<string, int>> CountProductsPerInitiativeAsync(
        IReadOnlyCollection<string> initiativeNames,
        CancellationToken cancellationToken)
    {
        if (initiativeNames.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        string list = string.Join(", ", initiativeNames.Select(Quote));
        await using NpgsqlDataReader reader = await ExecuteReaderAsync(
            $"MATCH (p:{AgeSession.ProductLabel})-[e:{AgeSession.HasInitiativeEdgeLabel}]->(i:{AgeSession.InitiativeLabel}) WHERE i.name IN [{list}] RETURN i.name, count(p)",
            "(name agtype, count agtype)",
            cancellationToken);
        var result = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result[ReadString(reader, 0)] = (int)ParseAgtypeInteger(ReadString(reader, 1));
        }

        return result;
    }

    private async Task<bool> UpsertWithinTransactionAsync(
        string name,
        string? description,
        string? status,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(cancellationToken);
        if (await HasCaseFoldedDuplicateAsync(name, cancellationToken))
        {
            throw new ConflictException(
                $"A product whose name case-folds to '{name}' already exists; use that spelling or register a distinct name.");
        }

        bool existed = await ExistsAsync(name, cancellationToken);
        if (existed)
        {
            var sets = new List<string>();
            if (description is not null)
            {
                sets.Add($"p.description = {Quote(description)}");
            }

            if (status is not null)
            {
                sets.Add($"p.status = {Quote(status)}");
            }

            if (sets.Count > 0)
            {
                string update = $"""
                    MATCH (p:{AgeSession.ProductLabel})
                    WHERE p.name = {Quote(name)}
                    SET {string.Join(", ", sets)}
                    RETURN 1
                    """;
                await ExecuteScalarAsync(update, cancellationToken);
            }

            return false;
        }

        string? descriptionClause = description is null ? null : $", description: {Quote(description)}";
        string create = $"CREATE (p:{AgeSession.ProductLabel} {{name: {Quote(name)}, status: {Quote(status ?? "active")}{descriptionClause}}}) RETURN 1";
        await ExecuteScalarAsync(create, cancellationToken);
        return true;
    }

    private async Task<bool> LinkInitiativeWithinTransactionAsync(
        string productName,
        string initiativeName,
        string reason,
        string source,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(cancellationToken);
        if (await HasInitiativeLinkAsync(productName, initiativeName, cancellationToken))
        {
            return false;
        }

        string cypher = $$"""
            MATCH (p:{{AgeSession.ProductLabel}})
            WHERE p.name = {{Quote(productName)}}
            MERGE (i:{{AgeSession.InitiativeLabel}} {name: {{Quote(initiativeName)}}})
            CREATE (p)-[:{{AgeSession.HasInitiativeEdgeLabel}} {reason: {{Quote(reason)}}, source: {{Quote(source)}}, declaredAt: {{Quote(SystemClock.Instance.GetCurrentInstant().ToString())}}}]->(i)
            RETURN 1
            """;
        string? written = await ExecuteScalarAsync(cypher, cancellationToken);
        if (written is null)
        {
            throw new ConflictException($"Product '{productName}' is no longer registered; the link was not created.");
        }

        return true;
    }

    private async Task<bool> UnlinkInitiativeWithinTransactionAsync(
        string productName,
        string initiativeName,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(cancellationToken);
        string cypher = $$"""
            MATCH (p:{{AgeSession.ProductLabel}})-[e:{{AgeSession.HasInitiativeEdgeLabel}}]->(i:{{AgeSession.InitiativeLabel}})
            WHERE p.name = {{Quote(productName)}} AND i.name = {{Quote(initiativeName)}}
            DELETE e
            RETURN count(e)
            """;
        string? count = await ExecuteScalarAsync(cypher, cancellationToken);
        return ParseAgtypeInteger(count) > 0;
    }

    private async Task<RepoLinkResult> LinkRepoWithinTransactionAsync(
        string productName,
        string repoName,
        string reason,
        string source,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(cancellationToken);
        IReadOnlyList<string> existingProducts = await GetRepoProductsAsync(repoName, cancellationToken);
        if (existingProducts.Count > 0)
        {
            string owner = existingProducts[0];
            if (string.Equals(owner, productName, StringComparison.Ordinal))
            {
                return new RepoLinkResult(false, null);
            }

            return new RepoLinkResult(false, owner);
        }

        string cypher = $$"""
            MATCH (p:{{AgeSession.ProductLabel}})
            WHERE p.name = {{Quote(productName)}}
            MERGE (r:{{AgeSession.RepoLabel}} {name: {{Quote(repoName)}}})
            CREATE (p)-[:{{AgeSession.HasRepoEdgeLabel}} {reason: {{Quote(reason)}}, source: {{Quote(source)}}, declaredAt: {{Quote(SystemClock.Instance.GetCurrentInstant().ToString())}}}]->(r)
            RETURN 1
            """;
        string? written = await ExecuteScalarAsync(cypher, cancellationToken);
        if (written is null)
        {
            throw new ConflictException($"Product '{productName}' is no longer registered; the link was not created.");
        }

        return new RepoLinkResult(true, null);
    }

    private async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken);
        }

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        T result = await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<string>> GetProductNamesAsync(CancellationToken cancellationToken) =>
        await ReadColumnAsync($"MATCH (p:{AgeSession.ProductLabel}) RETURN p.name", "name", cancellationToken);

    private async Task<bool> HasCaseFoldedDuplicateAsync(string name, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> existing = await GetProductNamesAsync(cancellationToken);
        foreach (string n in existing)
        {
            if (!string.Equals(n, name, StringComparison.Ordinal)
                && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HasInitiativeLinkAsync(string productName, string initiativeName, CancellationToken cancellationToken)
    {
        string cypher = $"""
            MATCH (p:{AgeSession.ProductLabel})-[e:{AgeSession.HasInitiativeEdgeLabel}]->(i:{AgeSession.InitiativeLabel})
            WHERE p.name = {Quote(productName)} AND i.name = {Quote(initiativeName)}
            RETURN count(e)
            """;
        string? count = await ExecuteScalarAsync(cypher, cancellationToken);
        return ParseAgtypeInteger(count) > 0;
    }

    private async Task<IReadOnlyList<string>> GetRepoProductsAsync(string repoName, CancellationToken cancellationToken) =>
        await ReadColumnAsync(
            $"MATCH (r:{AgeSession.RepoLabel})<-[e:{AgeSession.HasRepoEdgeLabel}]-(p:{AgeSession.ProductLabel}) WHERE r.name = {Quote(repoName)} RETURN p.name",
            "name",
            cancellationToken);

    private async Task<int> CountHopsForProductAsync(
        string productName,
        string edgeLabel,
        CancellationToken cancellationToken)
    {
        string cypher = $$"""
            MATCH (p:{{AgeSession.ProductLabel}})-[e:{{edgeLabel}}]->()
            WHERE p.name = {{Quote(productName)}}
            RETURN count(e)
            """;
        string? count = await ExecuteScalarAsync(cypher, cancellationToken);
        return (int)ParseAgtypeInteger(count);
    }

    private async Task<IReadOnlyDictionary<string, int>> CountHopsAsync(
        string edgeLabel,
        CancellationToken cancellationToken)
    {
        await using NpgsqlDataReader reader = await ExecuteReaderAsync(
            $"MATCH (p:{AgeSession.ProductLabel})-[e:{edgeLabel}]->() RETURN p.name, count(e)",
            "(name agtype, count agtype)",
            cancellationToken);
        var result = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result[ReadString(reader, 0)] = (int)ParseAgtypeInteger(ReadString(reader, 1));
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> ReadColumnAsync(
        string cypher,
        string column,
        CancellationToken cancellationToken)
    {
        await using NpgsqlDataReader reader = await ExecuteReaderAsync(
            cypher,
            $"({column} agtype)",
            cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadString(reader, 0));
        }

        return values;
    }

    private async Task AcquireLockAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateRelationalCommandAsync(
            $"SELECT pg_advisory_xact_lock({AdvisoryLockKey}, {AdvisoryLockId});",
            cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlCommand> CreateRelationalCommandAsync(string sql, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return new NpgsqlCommand(sql, connection, transaction);
    }

    private async Task<NpgsqlDataReader> ExecuteReaderAsync(
        string cypher,
        string resultColumns,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(cypher, resultColumns, cancellationToken);
        return await command.ExecuteReaderAsync(cancellationToken);
    }

    private async Task<string?> ExecuteScalarAsync(string cypher, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(cypher, "(v agtype)", cancellationToken);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private async Task<NpgsqlCommand> CreateCommandAsync(
        string cypher,
        string resultColumns,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

        // The cypher() result is aliased to the column names in resultColumns, and each is read back
        // as its ::text rendering. SELECT must name those aliases, not a hardcoded `v` — a
        // `(name agtype, ...)` projection has no column called `v`.
        string selectList = string.Join(
            ", ",
            resultColumns.Trim('(', ')').Split(',').Select(c => $"{c.Trim().Split(' ')[0]}::text"));

        return new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = $"""
                SELECT {selectList} FROM ag_catalog.cypher('{AgeSession.GraphName}', {DollarWrap(cypher)}) AS {resultColumns};
                """,
        };
    }

    private static string ReadString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string Quote(string value) => CypherLiteral.Quote(value);

    private static string DollarWrap(string cypher) => CypherLiteral.DollarWrap(cypher);

    private static long ParseAgtypeInteger(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        int typeSuffix = value.IndexOf("::", StringComparison.Ordinal);
        ReadOnlySpan<char> number = typeSuffix >= 0 ? value.AsSpan(0, typeSuffix) : value.AsSpan();
        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : 0;
    }
}
