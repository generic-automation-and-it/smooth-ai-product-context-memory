using FluentValidation.TestHelper;
using SmoothAiProductContextMemory.Application.Features.Preflight;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features;

public class PreflightValidatorTests
{
    private readonly Preflight.Validator _validator = new();

    [Fact]
    public void Rejects_letter_free_description()
    {
        var request = new Preflight.Request(
            [new Preflight.Candidate("!!! ###", null, null, null)]);

        _validator.TestValidate(request).ShouldHaveValidationErrorFor("Candidates[0].Description");
    }

    [Fact]
    public void Accepts_valid_candidate()
    {
        var request = new Preflight.Request(
            [new Preflight.Candidate("PostgreSQL stores our search index.", "architecture", null, null)]);

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
