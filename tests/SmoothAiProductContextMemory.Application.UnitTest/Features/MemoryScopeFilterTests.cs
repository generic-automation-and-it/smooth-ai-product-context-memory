using SmoothAiProductContextMemory.Application.Common.Retrieval;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class MemoryScopeFilterTests
{
    [Fact]
    public void Open_product_search_omits_program()
    {
        MemoryScopeFilter.IncludeGroup(MemoryGroup.ScopeDimensionValue.Program, requestedScope: null, hasGroupContext: false)
            .ShouldBeFalse();
        MemoryScopeFilter.IncludeGroup(MemoryGroup.ScopeDimensionValue.Product, requestedScope: null, hasGroupContext: false)
            .ShouldBeTrue();
        MemoryScopeFilter.IncludeGroup(MemoryGroup.ScopeDimensionValue.Self, requestedScope: null, hasGroupContext: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void Explicit_program_scope_includes_program()
    {
        MemoryScopeFilter.IncludeGroup(
                MemoryGroup.ScopeDimensionValue.Program,
                requestedScope: MemoryGroup.ScopeDimensionValue.Program,
                hasGroupContext: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void Explicit_scope_excludes_other_dimensions()
    {
        MemoryScopeFilter.IncludeGroup(
                MemoryGroup.ScopeDimensionValue.Product,
                requestedScope: MemoryGroup.ScopeDimensionValue.Program,
                hasGroupContext: false)
            .ShouldBeFalse();
    }

    [Fact]
    public void Group_context_includes_program()
    {
        MemoryScopeFilter.IncludeGroup(MemoryGroup.ScopeDimensionValue.Program, requestedScope: null, hasGroupContext: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void Open_product_search_flag()
    {
        MemoryScopeFilter.IsOpenProductSearch(null, hasGroupContext: false).ShouldBeTrue();
        MemoryScopeFilter.IsOpenProductSearch("program", hasGroupContext: false).ShouldBeFalse();
        MemoryScopeFilter.IsOpenProductSearch(null, hasGroupContext: true).ShouldBeFalse();
    }

    /// <summary>
    /// The plan is what the provider translates into SQL, so it is asserted directly — not only
    /// through the predicate built on top of it.
    /// </summary>
    [Fact]
    public void Plan_expresses_the_rule_as_data()
    {
        MemoryScopeFilter.ScopeFilterPlan open = MemoryScopeFilter.Plan(null, hasGroupContext: false);
        open.RequiredDimension.ShouldBeNull();
        open.ExcludedDimensions.ShouldBe([MemoryGroup.ScopeDimensionValue.Program]);

        MemoryScopeFilter.ScopeFilterPlan explicitScope =
            MemoryScopeFilter.Plan(MemoryGroup.ScopeDimensionValue.Customer, hasGroupContext: false);
        explicitScope.RequiredDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Customer);
        explicitScope.ExcludedDimensions.ShouldBeEmpty();

        MemoryScopeFilter.ScopeFilterPlan inGroup = MemoryScopeFilter.Plan(null, hasGroupContext: true);
        inGroup.RequiredDimension.ShouldBeNull();
        inGroup.ExcludedDimensions.ShouldBeEmpty();
    }
}
