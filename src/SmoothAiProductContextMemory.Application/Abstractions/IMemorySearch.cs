using NodaTime;
using SmoothAiProductContextMemory.Application.Common.Models;

namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Server-side hybrid retrieval over the cheap fields. Every predicate in
/// <see cref="MemorySearchCriteria"/> must be executed by the database — the implementation lives in
/// Infrastructure because matching the full-text and array indexes requires provider-specific
/// operators (<c>to_tsvector</c>, <c>&amp;&amp;</c>/<c>@&gt;</c>) that Application must not reference.
/// </summary>
public interface IMemorySearch
{
    Task<IReadOnlyList<CheapMemory>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// The evidence a retrieval would match, without shipping the rows. Used by the read-path inspect
    /// flag (<c>QueryMemories.Request.Inspect</c>): it grounds "what the read path would return" with the
    /// same predicates and a real match count, but carries none of the memory content and never records
    /// recall feedback. Never skips scope enforcement — it runs the full predicate pipeline.
    /// </summary>
    Task<MemorySearchInspection> InspectAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>
/// The criteria-level evidence a search would match. <see cref="TsQuery"/> is the decomposed free-text
/// predicate (the lexemes the FTS index compares against); <see cref="MatchedCount"/> and
/// <see cref="Limited"/> describe how many rows the read path would return before and after the ceiling.
/// The richer identity/traversal evidence (group, ticket, product, scope plan) is assembled by the query
/// handler, since it resolves those before constructing the criteria.
/// </summary>
public sealed record MemorySearchInspection(string? TsQuery, int MatchedCount, bool Limited);

/// <summary>
/// A fully resolved retrieval request. The handler resolves group/ticket identity and the scope rule
/// before constructing this, so the provider only translates predicates.
/// </summary>
public sealed record MemorySearchCriteria
{
    /// <summary>Free text matched against the indexed <c>to_tsvector</c> expressions.</summary>
    public string? FreeText { get; init; }

    public IReadOnlyList<string> Facets { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// How the facet and tag arrays match a row. <see cref="FacetMatchModeValue.Any"/> (the default)
    /// returns rows carrying <b>any</b> requested value; <see cref="FacetMatchModeValue.All"/> returns
    /// only rows carrying <b>every</b> requested value (array containment).
    /// </summary>
    public string FacetMatchMode { get; init; } = FacetMatchModeValue.Any;

    public string? Kind { get; init; }

    /// <summary>Exact status match. When null, <see cref="ExcludeProposed"/> applies instead.</summary>
    public string? Status { get; init; }

    public bool ExcludeProposed { get; init; } = true;

    /// <summary>Only groups with this scope dimension. From the scope plan, never from raw input.</summary>
    public string? RequiredScopeDimension { get; init; }

    /// <summary>Scope dimensions that must not be returned. From the scope plan.</summary>
    public IReadOnlyList<string> ExcludedScopeDimensions { get; init; } = [];

    public Guid? GroupUuid { get; init; }

    /// <summary>Surrogate group key, already resolved from a ticket lookup.</summary>
    public long? GroupId { get; init; }

    /// <summary>
    /// Memory uuids carrying an <c>ABOUT</c> reference to the ticket, disjunctive with
    /// <see cref="GroupId"/> — a reference row is selected without being in the owner group. Distinct
    /// from <see cref="UuidFilter"/>, which is conjunctive; the two meanings are kept explicit.
    /// </summary>
    public IReadOnlyList<Guid>? ReferencedUuids { get; init; }

    /// <summary>Scope dimension required of the reference branch of a union (D2).</summary>
    public string? ReferencedRequiredScopeDimension { get; init; }

    /// <summary>Scope dimensions the reference branch of a union must not return (D2).</summary>
    public IReadOnlyList<string> ReferencedExcludedScopeDimensions { get; init; } = [];

    /// <summary>
    /// Narrow the search to these memory identities before the row ceiling is applied. Lets a ticket
    /// (or other pre-resolved) anchor conjunctively restrict the search in SQL rather than by filtering
    /// an already-truncated result set in memory (HLD-005 NFR-03).
    /// </summary>
    public IReadOnlyList<Guid>? UuidFilter { get; init; }

    public string? Repo { get; init; }

    public string? InitiativeName { get; init; }

    /// <summary>
    /// Initiative names resolved from a product, applied as <c>initiative IN (...)</c>. Combined with
    /// <see cref="RepoNames"/> as a single OR predicate: a group matches when its initiative is in
    /// <see cref="InitiativeNames"/> OR its repo is in <see cref="RepoNames"/>. Only set by the product
    /// read path; the single <see cref="InitiativeName"/> is the direct query form.
    /// </summary>
    public IReadOnlyList<string>? InitiativeNames { get; init; }

    /// <summary>Repo names resolved from a product; see <see cref="InitiativeNames"/> for the OR semantics.</summary>
    public IReadOnlyList<string>? RepoNames { get; init; }

    /// <summary>Business-time instant the claim must be valid at. Null means no temporal narrowing.</summary>
    public Instant? AsOf { get; init; }

    public bool CurrentOnly { get; init; } = true;

    public int Limit { get; init; } = MemorySearchDefaults.Limit;
}

public static class MemorySearchDefaults
{
    public const int Limit = 50;

    public const int MaxLimit = 200;
}

/// <summary>
/// Wire spellings of the facet/tag array match mode. <see cref="Any"/> is the default so the
/// semantic-dedup recall step unifies rows across a batch's facets; containment is the deliberate
/// narrowing form, available as <see cref="All"/>.
/// </summary>
public static class FacetMatchModeValue
{
    public const string Any = "any";

    public const string All = "all";

    public static readonly string[] Allowed = [Any, All];
}
