using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Groups;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class ResolveGroupValidatorTests
{
    private readonly ResolveGroup.Validator _validator = new();

    /// <summary>
    /// Group creation is the other door tickets enter through, and it shares the null-url hole with
    /// <see cref="UpdateGroup"/>: an omitted JSON <c>url</c> binds to null and is persisted into
    /// jsonb, where the export renderer dereferences it.
    /// </summary>
    [Fact]
    public void Rejects_a_ticket_with_a_null_url()
    {
        ResolveGroup.Request request = new(
            [new TicketInput("jira", "ACM-1", null!)], null, null, null, null, null, null, null);

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("Tickets[0].Url");
    }

    [Fact]
    public void Accepts_a_ticket_with_an_empty_url()
    {
        ResolveGroup.Request request = new(
            [new TicketInput("local", "wt-1", "")], null, null, null, null, null, null, null);

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Rejects_a_ticket_without_a_provider_or_key()
    {
        ResolveGroup.Request request = new(
            [new TicketInput("", "", "")], null, null, null, null, null, null, null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Tickets[0].Provider");
        result.ShouldHaveValidationErrorFor("Tickets[0].Key");
    }

    [Fact]
    public void Requires_a_scope_identifier_for_a_narrowed_scope()
    {
        ResolveGroup.Request request = new(
            null, null, null, null, "program", null, null, null);

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.ScopeIdentifier);
    }
}
