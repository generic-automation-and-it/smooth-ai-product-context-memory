using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Features.Memories;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class QueryMemoriesValidatorTests
{
    private readonly QueryMemories.Validator _validator = new();

    [Fact]
    public void Rejects_limit_outside_the_allowed_range()
    {
        _validator.TestValidate(Request() with { Limit = 0 })
            .ShouldHaveValidationErrorFor(x => x.Limit);
        _validator.TestValidate(Request() with { Limit = MemorySearchDefaults.MaxLimit + 1 })
            .ShouldHaveValidationErrorFor(x => x.Limit);
    }

    /// <summary>A half-specified ticket would silently widen the search instead of narrowing it.</summary>
    [Fact]
    public void Rejects_a_partial_ticket_reference()
    {
        _validator.TestValidate(Request() with { TicketProvider = "jira" })
            .ShouldHaveValidationErrorFor(x => x.TicketKey);
        _validator.TestValidate(Request() with { TicketKey = "ACM-1" })
            .ShouldHaveValidationErrorFor(x => x.TicketProvider);
    }

    [Fact]
    public void Accepts_an_empty_query()
    {
        _validator.TestValidate(Request()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Accepts_a_full_target_query()
    {
        QueryMemories.Request request = Request() with
        {
            Query = "storage",
            Facets = ["architecture"],
            Tags = ["adr"],
            Kind = "decision",
            Status = "approved",
            ScopeDimension = "product",
            Repo = "kingstown",
            InitiativeName = "to-be-decided",
            AsOf = DateTimeOffset.UtcNow,
            Limit = 25,
        };

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    private static QueryMemories.Request Request() =>
        new(null, null, null, null, null, null, null, null, null, null, null);
}
