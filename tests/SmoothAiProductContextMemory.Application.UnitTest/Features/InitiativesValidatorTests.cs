using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Initiatives;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class GetInitiativesValidatorTests
{
    private readonly GetInitiatives.Validator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("active")]
    [InlineData("archived")]
    public void Accepts_status_filter_values(string? status)
    {
        _validator.TestValidate(new GetInitiatives.Request(status)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ACTIVE")]
    [InlineData("approved")]
    public void Rejects_unknown_status_filter(string status)
    {
        _validator.TestValidate(new GetInitiatives.Request(status))
            .ShouldHaveValidationErrorFor(x => x.Status);
    }
}

public class UpsertInitiativeValidatorTests
{
    private readonly UpsertInitiative.Validator _validator = new();

    [Fact]
    public void Requires_nonempty_name()
    {
        _validator.TestValidate(new UpsertInitiative.Request(" ")).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("archived")]
    public void Accepts_valid_status(string status)
    {
        _validator.TestValidate(new UpsertInitiative.Request("wt", Status: status)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("draft")]
    [InlineData("ACTIVE")]
    public void Rejects_unknown_status(string status)
    {
        _validator.TestValidate(new UpsertInitiative.Request("wt", Status: status))
            .ShouldHaveValidationErrorFor(x => x.Status);
    }
}
