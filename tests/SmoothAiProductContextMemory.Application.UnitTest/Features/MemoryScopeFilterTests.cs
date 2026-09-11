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
}
