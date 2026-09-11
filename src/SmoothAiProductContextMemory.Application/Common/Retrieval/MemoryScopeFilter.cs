using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Common.Retrieval;

/// <summary>
/// Programme-scoped memories are never citable as shipped product fact on an open search.
/// Explicit scope, group, or ticket is in-group context and may include programme rows.
/// </summary>
public static class MemoryScopeFilter
{
    public static bool IncludeGroup(
        string scopeDimension,
        string? requestedScope,
        bool hasGroupContext)
    {
        if (hasGroupContext)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            return string.Equals(scopeDimension, requestedScope, StringComparison.Ordinal);
        }

        return !string.Equals(scopeDimension, MemoryGroup.ScopeDimensionValue.Program, StringComparison.Ordinal);
    }

    public static bool IsOpenProductSearch(string? requestedScope, bool hasGroupContext) =>
        !hasGroupContext && string.IsNullOrWhiteSpace(requestedScope);
}
