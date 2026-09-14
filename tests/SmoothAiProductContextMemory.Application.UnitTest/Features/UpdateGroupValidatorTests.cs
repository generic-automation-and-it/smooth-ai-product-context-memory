using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Groups;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class UpdateGroupValidatorTests
{
    private readonly UpdateGroup.Validator _validator = new();

    [Fact]
    public void Rejects_empty_group_and_unknown_scope()
    {
        UpdateGroup.Request request = new(Guid.Empty, null, null, null, "galaxy", null);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.GroupUuid);
        result.ShouldHaveValidationErrorFor(x => x.ScopeDimension);
    }

    [Fact]
    public void Accepts_a_partial_update()
    {
        UpdateGroup.Request request = new(Guid.NewGuid(), "kingstown", null, null, null, null);

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Accepts_a_scope_change_with_an_identifier()
    {
        UpdateGroup.Request request = new(
            Guid.NewGuid(),
            null,
            null,
            null,
            MemoryGroup.ScopeDimensionValue.Program,
            "atlas");

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Rejects_a_ticket_without_a_provider_or_key()
    {
        UpdateGroup.Request request = new(
            Guid.NewGuid(),
            null,
            null,
            null,
            null,
            null,
            [new TicketInput("", "", "https://example.com/ACM-1")]);

        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Tickets[0].Provider");
        result.ShouldHaveValidationErrorFor("Tickets[0].Key");
    }

    [Fact]
    public void Accepts_wellformed_tickets()
    {
        UpdateGroup.Request request = new(
            Guid.NewGuid(),
            null,
            null,
            null,
            null,
            null,
            [new TicketInput("jira", "ACM-1", "https://example.com/ACM-1")]);

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
