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

    /// <summary>
    /// Hop visibility is a separate question from endpoint narrowing, and conflating them inverts the
    /// rule: <see cref="MemoryScopeFilter.Plan"/> returns an empty excluded list for every explicit
    /// dimension, so a traversal gate driven off it filters nothing exactly when the caller narrows.
    /// </summary>
    [Fact]
    public void HiddenDimensions_never_widen_when_the_caller_narrows()
    {
        // Declaring nothing hides programme.
        MemoryScopeFilter.HiddenDimensions(null, hasGroupContext: false)
            .ShouldBe([MemoryGroup.ScopeDimensionValue.Program]);

        // Declaring another dimension must hide it too, unlike Plan's excluded list.
        foreach (string declared in new[]
                 {
                     MemoryGroup.ScopeDimensionValue.Product,
                     MemoryGroup.ScopeDimensionValue.Customer,
                     MemoryGroup.ScopeDimensionValue.Self,
                 })
        {
            MemoryScopeFilter.HiddenDimensions(declared, hasGroupContext: false)
                .ShouldBe([MemoryGroup.ScopeDimensionValue.Program]);
            MemoryScopeFilter.Plan(declared, hasGroupContext: false).ExcludedDimensions.ShouldBeEmpty();
        }
    }

    [Fact]
    public void HiddenDimensions_hides_nothing_once_programme_is_declared_or_in_group()
    {
        MemoryScopeFilter.HiddenDimensions(MemoryGroup.ScopeDimensionValue.Program, hasGroupContext: false)
            .ShouldBeEmpty();
        MemoryScopeFilter.HiddenDimensions(null, hasGroupContext: true).ShouldBeEmpty();
    }

    /// <summary>
    /// The hop rule and the single-memory rule must agree about programme, or the traversal and the blob
    /// proxy would disagree about the same memory.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(MemoryGroup.ScopeDimensionValue.Product)]
    [InlineData(MemoryGroup.ScopeDimensionValue.Customer)]
    [InlineData(MemoryGroup.ScopeDimensionValue.Self)]
    [InlineData(MemoryGroup.ScopeDimensionValue.Program)]
    public void HiddenDimensions_agrees_with_IncludeGroup_about_programme(string? declared)
    {
        bool programmeVisibleToHops = !MemoryScopeFilter
            .HiddenDimensions(declared, hasGroupContext: false)
            .Contains(MemoryGroup.ScopeDimensionValue.Program, StringComparer.Ordinal);
        bool programmeVisibleToReads = MemoryScopeFilter.IncludeGroup(
            MemoryGroup.ScopeDimensionValue.Program, declared, hasGroupContext: false);

        programmeVisibleToHops.ShouldBe(programmeVisibleToReads);
    }
}
