using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Executes retrieval entirely in PostgreSQL.
/// </summary>
/// <remarks>
/// Lives in Infrastructure because the index-matching operators are provider-specific: the full-text
/// predicates must reproduce the exact <c>to_tsvector('simple', … || ' ' || …)</c> expressions the GIN
/// indexes were built over. Projecting straight to <see cref="CheapMemory"/> also keeps blob addresses
/// and the rest of the version chain out of the result set.
/// <para>
/// Two deliberate departures from plain LINQ:
/// </para>
/// <list type="bullet">
/// <item>
/// Facet and tag matching goes through a small <c>FROM</c> fragment so the predicate is array
/// containment (<c>@&gt;</c>), which the GIN indexes on those columns serve. LINQ's
/// <c>List.Contains</c> translates to <c>= ANY(column)</c>, which they do not. Column names are
/// literals; every value is a parameter.
/// </item>
/// <item>
/// The as-of predicate is scalar rather than range containment: <c>ix_memory_version_validity</c> is
/// a GIST index over <c>tstzrange(valid_from, valid_until)</c> and LINQ cannot construct a range from
/// two columns. It is always combined with a narrowing predicate. Revisit if EXPLAIN shows this
/// dominating.
/// </item>
/// </list>
/// </remarks>
public sealed class NpgsqlMemorySearch(SmoothAiProductContextMemoryDbContext db) : IMemorySearch
{
    private const string TextSearchConfig = "simple";

    public async Task<IReadOnlyList<CheapMemory>> SearchAsync(
        MemorySearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var rows = from version in db.MemoryVersions.AsNoTracking()
                   join memory in ClassifiedMemories(criteria) on version.MemoryId equals memory.Id
                   join grp in db.MemoryGroups on memory.GroupId equals grp.Id
                   select new { version, memory, grp };

        if (criteria.CurrentOnly)
        {
            rows = rows.Where(x => x.version.IsCurrent);
        }

        if (criteria.Kind is { } kind)
        {
            rows = rows.Where(x => x.version.Kind == kind);
        }

        if (criteria.Status is { } status)
        {
            rows = rows.Where(x => x.version.Status == status);
        }
        else if (criteria.ExcludeProposed)
        {
            rows = rows.Where(x => x.version.Status != MemoryVersion.MemoryVersionStatus.Proposed);
        }

        if (criteria.AsOf is { } asOf)
        {
            rows = rows.Where(x => x.version.ValidFrom <= asOf
                && (x.version.ValidUntil == null || x.version.ValidUntil > asOf));
        }

        if (criteria.RequiredScopeDimension is { } required)
        {
            rows = rows.Where(x => x.grp.ScopeDimension == required);
        }

        if (criteria.ExcludedScopeDimensions.Count > 0)
        {
            List<string> excluded = [.. criteria.ExcludedScopeDimensions];
            rows = rows.Where(x => !excluded.Contains(x.grp.ScopeDimension));
        }

        if (criteria.GroupUuid is { } groupUuid)
        {
            rows = rows.Where(x => x.grp.Uuid == groupUuid);
        }

        if (criteria.GroupId is { } groupId)
        {
            rows = rows.Where(x => x.memory.GroupId == groupId);
        }

        if (criteria.Repo is { } repo)
        {
            rows = rows.Where(x => x.grp.Repo == repo);
        }

        if (criteria.InitiativeName is { } initiative)
        {
            rows = rows.Where(x => db.Initiatives.Any(i => i.Id == x.grp.InitiativeId && i.Name == initiative));
        }

        if (criteria.FreeText is { } text)
        {
            // plainto_tsquery stays inside the expression tree. Hoisting it into a local would call it
            // on the client, where it throws — the provider only translates it in place.
            rows = rows.Where(x =>
                EF.Functions.ToTsVector(TextSearchConfig, x.memory.Name + " " + x.memory.Description)
                    .Matches(EF.Functions.PlainToTsQuery(TextSearchConfig, text))
                || EF.Functions.ToTsVector(TextSearchConfig, x.version.Statement + " " + x.version.ContentSummary)
                    .Matches(EF.Functions.PlainToTsQuery(TextSearchConfig, text)));
        }

        return await rows
            .OrderByDescending(x => x.version.ValidFrom)
            .ThenBy(x => x.memory.Uuid)
            .ThenBy(x => x.version.Version)
            .Take(criteria.Limit)
            .Select(x => new CheapMemory(
                x.memory.Uuid,
                x.grp.Uuid,
                x.memory.Name,
                x.memory.Description,
                x.version.Statement,
                x.version.ContentSummary,
                x.version.Kind,
                x.memory.Facets,
                x.memory.Tags,
                x.version.Status,
                x.version.Confidence,
                x.grp.ScopeDimension,
                x.grp.ScopeIdentifier,
                x.version.ValidFrom,
                x.version.ValidUntil,
                x.version.Version,
                x.version.IsCurrent))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The memory set narrowed by facet and tag containment. Falls back to the plain set when neither
    /// is requested, so the common query carries no extra subquery.
    /// </summary>
    private IQueryable<Memory> ClassifiedMemories(MemorySearchCriteria criteria)
    {
        if (criteria.Facets.Count == 0 && criteria.Tags.Count == 0)
        {
            return db.Memories.AsNoTracking();
        }

        var conditions = new List<string>();
        var parameters = new List<object>();

        if (criteria.Facets.Count > 0)
        {
            conditions.Add("facets @> {" + parameters.Count.ToString(CultureInfo.InvariantCulture) + "}");
            parameters.Add(criteria.Facets.ToArray());
        }

        if (criteria.Tags.Count > 0)
        {
            conditions.Add("tags @> {" + parameters.Count.ToString(CultureInfo.InvariantCulture) + "}");
            parameters.Add(criteria.Tags.ToArray());
        }

        // The SQL text is assembled from literals and positional placeholders only — the facet and tag
        // values travel as parameters, so nothing caller-supplied reaches the statement text.
        string sql = "SELECT * FROM memory WHERE " + string.Join(" AND ", conditions);

        return db.Memories
            .FromSqlRaw(sql, [.. parameters])
            .AsNoTracking();
    }
}
