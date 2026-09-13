using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Retrieval;

/// <summary>
/// Programme-scoped memories are never citable as shipped product fact on an open search.
/// Explicit scope, group, or ticket is in-group context and may include programme rows.
/// </summary>
/// <remarks>
/// <see cref="Plan"/> is the single source of truth. It is expressed as data rather than as a
/// predicate so the same rule can be pushed into SQL by the retrieval provider instead of being
/// re-implemented there — the previous in-memory predicate was the reason scope filtering ran
/// client-side.
/// </remarks>
public static class MemoryScopeFilter
{
    /// <summary>
    /// The scope rule as data: at most one required dimension, plus dimensions to exclude.
    /// </summary>
    public sealed record ScopeFilterPlan(string? RequiredDimension, IReadOnlyList<string> ExcludedDimensions);

    private static readonly string[] ProgramOnly = [MemoryGroup.ScopeDimensionValue.Program];

    public static ScopeFilterPlan Plan(string? requestedScope, bool hasGroupContext)
    {
        if (hasGroupContext)
        {
            return new ScopeFilterPlan(null, []);
        }

        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            return new ScopeFilterPlan(requestedScope, []);
        }

        return new ScopeFilterPlan(null, ProgramOnly);
    }

    /// <summary>
    /// Dimensions that must not be disclosed even as an intermediate hop on a traversal.
    /// </summary>
    /// <remarks>
    /// <see cref="Plan"/>'s excluded list is empty for every explicit dimension, because there
    /// <see cref="ScopeFilterPlan.RequiredDimension"/> does the narrowing and exclusion is redundant. A
    /// traversal cannot use it that way: the required dimension narrows which *endpoints* come back and
    /// says nothing about the memories a path crosses on the way, so driving the hop gate off the
    /// excluded list stops filtering exactly when the caller narrows — declaring <c>product</c> would
    /// disclose programme hops that declaring nothing hides, inverting the consent model
    /// <c>GetMemoryBlob</c> enforces.
    /// <para>
    /// Programme is the dimension the rule exists to protect, so it is hidden unless the caller has
    /// declared it or holds in-group context. Other dimensions stay traversable: a provenance chain
    /// crossing scopes is normal, and narrowing a read must not silently truncate the chain that
    /// justifies it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> HiddenDimensions(string? requestedScope, bool hasGroupContext)
    {
        if (hasGroupContext)
        {
            return [];
        }

        return string.Equals(requestedScope, MemoryGroup.ScopeDimensionValue.Program, StringComparison.Ordinal)
            ? []
            : ProgramOnly;
    }

    /// <summary>
    /// Whether a group of the given scope dimension is visible under the plan. Kept as the readable
    /// statement of the rule and as the L0 assertion surface; derived from <see cref="Plan"/> so the
    /// two can never drift.
    /// </summary>
    public static bool IncludeGroup(string scopeDimension, string? requestedScope, bool hasGroupContext)
    {
        ScopeFilterPlan plan = Plan(requestedScope, hasGroupContext);

        if (plan.RequiredDimension is { } required)
        {
            return string.Equals(scopeDimension, required, StringComparison.Ordinal);
        }

        return !plan.ExcludedDimensions.Contains(scopeDimension, StringComparer.Ordinal);
    }

    public static bool IsOpenProductSearch(string? requestedScope, bool hasGroupContext) =>
        !hasGroupContext && string.IsNullOrWhiteSpace(requestedScope);
}
