using SmoothAiProductContextMemory.Application.Common.Models;

namespace SmoothAiProductContextMemory.Application.Abstractions;

/// <summary>
/// Server-side hybrid retrieval over the cheap fields. Every predicate in
/// <see cref="MemorySearchCriteria"/> must be executed by the database — the implementation lives in
/// Infrastructure because matching the full-text and array indexes requires provider-specific
/// operators (<c>to_tsvector</c>, <c>@&gt;</c>) that Application must not reference.
/// </summary>
public interface IMemorySearch
{
    Task<IReadOnlyList<CheapMemory>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken);
}

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

    public string? Repo { get; init; }

    public string? InitiativeName { get; init; }

    /// <summary>Business-time instant the claim must be valid at. Null means no temporal narrowing.</summary>
    public DateTimeOffset? AsOf { get; init; }

    public bool CurrentOnly { get; init; } = true;

    public int Limit { get; init; } = MemorySearchDefaults.Limit;
}

public static class MemorySearchDefaults
{
    public const int Limit = 50;

    public const int MaxLimit = 200;
}
